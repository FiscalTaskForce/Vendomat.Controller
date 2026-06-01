using System.Text.Json;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Tablet.Services;

public sealed class MachineRuntimeService : IMachineRuntimeService
{
    private const string RemoteCreditCommandType = "remote-credit";
    private const string DispenseCommandType = "dispense";
    private const string SanitationCommandType = "sanitation";
    private const string Esp32FirmwareUpdateCommandType = "esp32-firmware-update";

    private readonly IMachineSettingsRepository _settingsRepository;
    private readonly ISalesRepository _salesRepository;
    private readonly ILogRepository _logRepository;
    private readonly ISanitationRepository _sanitationRepository;
    private readonly IPairingService _pairingService;
    private readonly IBillValidatorGateway _billValidatorGateway;
    private readonly IEsp32Gateway _esp32Gateway;
    private readonly RemoteCommandJournal _remoteCommandJournal;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SensorSnapshot _sensor = new();
    private readonly SemaphoreSlim _validatorStartLock = new(1, 1);
    private readonly SemaphoreSlim _esp32StartLock = new(1, 1);

    private DispenseSessionState _session = new()
    {
        ActivityState = MachineActivityState.Ready,
        ActivePaymentMethod = PaymentMethod.Cash,
    };

    private Task? _validatorStartTask;
    private Task? _esp32StartTask;
    private DateTimeOffset _nextValidatorStartAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEsp32StartAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRealSensorUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDispenseProgressUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<Guid, DateTimeOffset> _executedCommandIds = [];
    private SaleTransaction? _activeSale;
    private MachineSettings? _activeDispenseSettings;
    private Guid? _activeDispenseCommandId;

    public MachineRuntimeService(
        IMachineSettingsRepository settingsRepository,
        ISalesRepository salesRepository,
        ILogRepository logRepository,
        ISanitationRepository sanitationRepository,
        IPairingService pairingService,
        IBillValidatorGateway billValidatorGateway,
        IEsp32Gateway esp32Gateway,
        RemoteCommandJournal remoteCommandJournal)
    {
        _settingsRepository = settingsRepository;
        _salesRepository = salesRepository;
        _logRepository = logRepository;
        _sanitationRepository = sanitationRepository;
        _pairingService = pairingService;
        _billValidatorGateway = billValidatorGateway;
        _esp32Gateway = esp32Gateway;
        _remoteCommandJournal = remoteCommandJournal;

        _billValidatorGateway.NoteRead += OnBillValidatorNoteRead;
        _billValidatorGateway.CreditAccepted += OnBillValidatorCreditAccepted;
        _billValidatorGateway.NoteRejected += OnBillValidatorNoteRejected;
        _esp32Gateway.SensorSnapshotReceived += OnEsp32SensorSnapshotReceived;
        _esp32Gateway.DispenseProgressReceived += OnEsp32DispenseProgressReceived;
        _esp32Gateway.DispenseCompleted += OnEsp32DispenseCompleted;
        _esp32Gateway.PortDetected += OnEsp32PortDetected;
    }

    public async Task<MachineStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        EnsureBillValidatorStarted();
        EnsureEsp32Started();
        var settings = await GetCompatibleSettingsAsync(cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            TickSensorSnapshot();
            NormalizeSessionUnsafe(settings);

            return new MachineStatusSnapshot
            {
                Settings = Clone(settings),
                Sensor = Clone(_sensor),
                Session = Clone(_session),
                GeneratedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<MachineDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var recentSales = (await _salesRepository.GetRecentAsync(12, cancellationToken))
            .Where(item => item.MachineId == status.Settings.MachineId)
            .OrderByDescending(item => item.StartedAtUtc)
            .ToList();
        var allSales = (await _salesRepository.GetAllAsync(cancellationToken))
            .Where(item => item.MachineId == status.Settings.MachineId)
            .OrderByDescending(item => item.StartedAtUtc)
            .ToList();
        var recentSanitations = (await _sanitationRepository.GetRecentAsync(12, cancellationToken))
            .Where(item => item.MachineId == status.Settings.MachineId)
            .OrderByDescending(item => item.StartedAtUtc)
            .ToList();
        var allSanitations = (await _sanitationRepository.GetAllAsync(cancellationToken))
            .Where(item => item.MachineId == status.Settings.MachineId)
            .OrderByDescending(item => item.StartedAtUtc)
            .ToList();

        var completedSales = allSales
            .Where(item => item.Status == SaleStatus.Completed)
            .OrderByDescending(item => item.CompletedAtUtc ?? item.StartedAtUtc)
            .ToList();
        var todayLocalDate = DateTime.Now.Date;
        var todayCompletedSales = completedSales
            .Where(item => (item.CompletedAtUtc ?? item.StartedAtUtc).LocalDateTime.Date == todayLocalDate)
            .ToList();
        var last7DaysThreshold = DateTimeOffset.UtcNow.AddDays(-7);

        return new MachineDashboardSnapshot
        {
            Status = status,
            Sales = new SalesDashboardSummary
            {
                TodayRevenue = todayCompletedSales.Sum(item => item.TotalAmount),
                TodayLiters = todayCompletedSales.Sum(item => item.DispensedLiters),
                TodayCompletedSales = todayCompletedSales.Count,
                TotalRevenue = completedSales.Sum(item => item.TotalAmount),
                TotalLiters = completedSales.Sum(item => item.DispensedLiters),
                TotalCompletedSales = completedSales.Count,
                LastSaleAtUtc = completedSales.FirstOrDefault()?.CompletedAtUtc ?? completedSales.FirstOrDefault()?.StartedAtUtc,
            },
            Sanitation = new SanitationDashboardSummary
            {
                TotalCycles = allSanitations.Count,
                CyclesLast7Days = allSanitations.Count(item => item.StartedAtUtc >= last7DaysThreshold),
                LastSanitationAtUtc = allSanitations.FirstOrDefault()?.StartedAtUtc,
            },
            RecentSales = recentSales,
            RecentSanitations = recentSanitations,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public Task<MachineSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        GetCompatibleSettingsAsync(cancellationToken);

    public async Task SaveSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
    {
        var existingSettings = await _settingsRepository.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CloudMachineToken))
        {
            settings.CloudMachineToken = existingSettings.CloudMachineToken;
        }

        if (string.IsNullOrWhiteSpace(settings.CompanionAccessToken))
        {
            settings.CompanionAccessToken = existingSettings.CompanionAccessToken;
        }

        if (string.IsNullOrWhiteSpace(settings.AdminPasscodeHash))
        {
            settings.AdminPasscodeHash = existingSettings.AdminPasscodeHash;
        }

        NormalizeSettings(settings);
        ApplyLegacyBillValidatorCompatibility(settings);
        await _settingsRepository.SaveAsync(settings, cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            NormalizeSessionUnsafe(settings);
        }
        finally
        {
            _sync.Release();
        }

        EnsureBillValidatorStarted(force: true);
        EnsureEsp32Started(force: true);

        await SafeLogAsync(new DeviceLogEntry
        {
            Category = "Settings",
            Message = "Setarile controllerului au fost actualizate.",
        }, cancellationToken);
    }

    public async Task SetPaymentMethodAsync(PaymentMethod paymentMethod, CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning)
            {
                throw new InvalidOperationException("Metoda de plata nu poate fi schimbata in timpul unei operatii.");
            }

            switch (paymentMethod)
            {
                case PaymentMethod.Cash:
                    if (!settings.CashPaymentEnabled)
                    {
                        throw new InvalidOperationException("Plata cu numerar este dezactivata.");
                    }

                    _session.ActivePaymentMethod = PaymentMethod.Cash;

                    if (_session.CurrentCreditAmount > 0)
                    {
                        _session.IsCardSelectionBlocked = true;
                        _session.RequestedLiters = settings.PricePerLiter <= 0
                            ? 0
                            : Math.Round(_session.CurrentCreditAmount / settings.PricePerLiter, 2);
                        _session.TotalAmount = _session.CurrentCreditAmount;
                    }
                    else
                    {
                        _session.IsCardSelectionBlocked = false;
                        _session.RequestedLiters = 0;
                        _session.TotalAmount = 0;
                    }

                    break;

                case PaymentMethod.Card:
                    if (!settings.CardPaymentEnabled)
                    {
                        throw new InvalidOperationException("Plata cu cardul este dezactivata.");
                    }

                    if (_session.IsCardSelectionBlocked || _session.CurrentCreditAmount > 0)
                    {
                        throw new InvalidOperationException("A fost introdus numerar. Finalizeaza sesiunea cash inainte de plata cu cardul.");
                    }

                    _session.ActivePaymentMethod = PaymentMethod.Card;
                    break;

                default:
                    throw new InvalidOperationException("Metoda de plata nu este suportata.");
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SetRequestedLitersAsync(decimal liters, CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);
        if (!settings.CardPaymentEnabled)
        {
            throw new InvalidOperationException("Plata cu cardul este dezactivata.");
        }

        var sanitizedLiters = Math.Max(0, Math.Round(liters, 2));

        await _sync.WaitAsync(cancellationToken);
        try
        {
            NormalizeSessionUnsafe(settings);

            if (_session.IsCardSelectionBlocked || _session.CurrentCreditAmount > 0)
            {
                throw new InvalidOperationException("A fost introdus numerar. Plata cu cardul nu mai este disponibila pentru sesiunea curenta.");
            }

            _session.ActivePaymentMethod = PaymentMethod.Card;
            _session.RequestedLiters = sanitizedLiters;
            _session.TotalAmount = Math.Round(sanitizedLiters * settings.PricePerLiter, 2);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task AddCreditAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            return;
        }

        var settings = await GetCompatibleSettingsAsync(cancellationToken);
        if (!settings.CashPaymentEnabled)
        {
            return;
        }

        await ApplyCreditAsync(amount, settings, cancellationToken);
    }

    public Task AddRemoteCreditAsync(decimal amount, CancellationToken cancellationToken = default) =>
        AddRemoteCreditAsync(new RemoteCreditRequest { Amount = amount }, cancellationToken);

    public async Task AddRemoteCreditAsync(RemoteCreditRequest request, CancellationToken cancellationToken = default)
    {
        if (!await TryBeginCommandAsync(request.CommandId, RemoteCreditCommandType, request, cancellationToken))
        {
            return;
        }

        try
        {
            var amount = request.Amount;
            if (amount < 0)
            {
                throw new InvalidOperationException("Valoarea creditului nu poate fi negativa.");
            }

            var settings = await GetCompatibleSettingsAsync(cancellationToken);
            await ApplyCreditAsync(amount, settings, cancellationToken, replaceExisting: true);

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "RemoteCredit",
                Message = $"Credit remote setat din companion: {amount:0.00} RON.",
            }, cancellationToken);
            await _remoteCommandJournal.CompleteAsync(request.CommandId, $"Credit actualizat la {amount:0.00} RON.", cancellationToken);
        }
        catch (Exception ex)
        {
            await _remoteCommandJournal.FailAsync(request.CommandId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task StartDispenseAsync(DispenseCommand command, CancellationToken cancellationToken = default)
    {
        if (!await TryBeginCommandAsync(command.CommandId, DispenseCommandType, command, cancellationToken))
        {
            return;
        }

        try
        {
            var settings = await GetCompatibleSettingsAsync(cancellationToken);
            var requestedLiters = Math.Round(command.RequestedLiters, 2);

            if (command.PaymentMethod == PaymentMethod.Cash && !settings.CashPaymentEnabled)
            {
                throw new InvalidOperationException("Plata cu numerar este dezactivata.");
            }

            if (command.PaymentMethod == PaymentMethod.Card && !settings.CardPaymentEnabled)
            {
                throw new InvalidOperationException("Plata cu cardul este dezactivata.");
            }

            if (requestedLiters <= 0)
            {
                throw new InvalidOperationException("Selecteaza cantitatea inainte de start.");
            }

            if (command.PaymentMethod == PaymentMethod.Cash && command.CreditAmount < requestedLiters * settings.PricePerLiter)
            {
                throw new InvalidOperationException("Creditul introdus este insuficient pentru cantitatea selectata.");
            }

            if (settings.RuntimeMode == RuntimeMode.Production && !settings.Esp32Enabled)
            {
                throw new InvalidOperationException("Dozarea in Production necesita ESP32 activ.");
            }

            var sale = new SaleTransaction
            {
                MachineId = settings.MachineId,
                RequestedLiters = requestedLiters,
                PricePerLiter = settings.PricePerLiter,
                TotalAmount = Math.Round(requestedLiters * settings.PricePerLiter, 2),
                PaymentMethod = command.PaymentMethod,
            };

            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning)
                {
                    throw new InvalidOperationException("Masina executa deja o operatie.");
                }

                _session.ActivityState = MachineActivityState.Dispensing;
                _session.ActivePaymentMethod = command.PaymentMethod;
                _session.RequestedLiters = requestedLiters;
                _session.TotalAmount = Math.Round(requestedLiters * settings.PricePerLiter, 2);
                _session.DispensedLiters = 0;
                _lastDispenseProgressUtc = DateTimeOffset.MinValue;
                _activeSale = sale;
                _activeDispenseSettings = Clone(settings);
                _activeDispenseCommandId = command.CommandId;
            }
            finally
            {
                _sync.Release();
            }

            EnsureEsp32Started();
            var commandSent = await TrySendDispenseCommandAsync(settings, requestedLiters);
            if (settings.RuntimeMode == RuntimeMode.Production)
            {
                if (!commandSent)
                {
                    await FailActiveDispenseAsync(settings, sale, "Comanda ESP32 nu a putut fi trimisa.", cancellationToken);
                    throw new InvalidOperationException("Comanda ESP32 nu a putut fi trimisa.");
                }

                return;
            }

            _ = Task.Run(() => SimulateDispenseAsync(settings, sale), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _remoteCommandJournal.FailAsync(command.CommandId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task RunSanitationAsync(SanitationRequest request, CancellationToken cancellationToken = default)
    {
        if (!await TryBeginCommandAsync(request.CommandId, SanitationCommandType, request, cancellationToken))
        {
            return;
        }

        try
        {
            var settings = await GetCompatibleSettingsAsync(cancellationToken);

            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_session.ActivityState == MachineActivityState.Dispensing)
                {
                    throw new InvalidOperationException("Curatarea nu poate porni in timpul unei dozari.");
                }

                _session.ActivityState = MachineActivityState.Cleaning;
            }
            finally
            {
                _sync.Release();
            }

            EnsureEsp32Started();
            _ = Task.Run(() => TrySendSanitationCommandAsync(request), CancellationToken.None);

            await _sanitationRepository.SaveAsync(new SanitationRecord
            {
                MachineId = settings.MachineId,
                Duration = request.Duration,
                Mode = request.Mode,
                PulseOn = request.PulseOn,
                PulseOff = request.PulseOff,
                Notes = "Rulat din interfata locala a controllerului.",
            }, cancellationToken);

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "Sanitation",
                Message = $"Curatare pornita in mod {request.Mode}.",
            }, cancellationToken);

            await Task.Delay(request.Duration, cancellationToken);

            await _sync.WaitAsync(cancellationToken);
            try
            {
                _session.ActivityState = MachineActivityState.Ready;
                _session.ActivePaymentMethod = ResolveDefaultPaymentMethod(settings);
            }
            finally
            {
                _sync.Release();
            }

            await _remoteCommandJournal.CompleteAsync(request.CommandId, $"Curatare {request.Mode} finalizata.", cancellationToken);
        }
        catch (Exception ex)
        {
            await _remoteCommandJournal.FailAsync(request.CommandId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task UpdateEsp32FirmwareAsync(Esp32FirmwareUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (!await TryBeginCommandAsync(request.CommandId, Esp32FirmwareUpdateCommandType, request, cancellationToken))
        {
            return;
        }

        try
        {
            if (request is null)
            {
                throw new InvalidOperationException("Cererea OTA pentru ESP32 lipseste.");
            }

            if (string.IsNullOrWhiteSpace(request.FirmwareUrl))
            {
                throw new InvalidOperationException("URL-ul firmware-ului ESP32 este obligatoriu.");
            }

            var settings = await GetCompatibleSettingsAsync(cancellationToken);
            if (!settings.Esp32Enabled)
            {
                throw new InvalidOperationException("ESP32 este dezactivat din setari.");
            }

            EnsureEsp32Started(force: true);
            await _esp32Gateway.SendFirmwareUpdateAsync(request, cancellationToken);

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "ESP32",
                Message = $"Update OTA trimis catre ESP32: {request.FirmwareUrl}",
            }, cancellationToken);

            await _remoteCommandJournal.CompleteAsync(
                request.CommandId,
                "Cererea de update OTA a fost trimisa catre ESP32. Verifica revenirea dispozitivului dupa reboot.",
                cancellationToken);
        }
        catch (Exception ex)
        {
            await _remoteCommandJournal.FailAsync(request.CommandId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task<PairingQrPayload> GeneratePairingAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CompanionAccessToken))
        {
            settings.CompanionAccessToken = CompanionAccessTokenSecurity.GenerateToken();
            await _settingsRepository.SaveAsync(settings, cancellationToken);
        }

        return await _pairingService.GenerateAsync(settings, cancellationToken);
    }

    public async Task<PairingClaimResult> ClaimPairingAsync(PairingClaimRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CompanionAccessToken))
        {
            settings.CompanionAccessToken = CompanionAccessTokenSecurity.GenerateToken();
            await _settingsRepository.SaveAsync(settings, cancellationToken);
        }

        var claimResult = await _pairingService.ClaimAsync(settings, request, cancellationToken);
        await SafeLogAsync(new DeviceLogEntry
        {
            Category = "Pairing",
            Message = "Aplicatia companion a fost imperecheata cu dozatorul.",
            Details = $"MachineId={claimResult.MachineId}; public={claimResult.PublicApiBaseUrl}; local={claimResult.LocalApiBaseUrl}",
        }, cancellationToken);

        return claimResult;
    }

    public async Task<bool> ValidateCompanionAccessTokenAsync(string? accessToken, CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CompanionAccessToken))
        {
            return true;
        }

        return CompanionAccessTokenSecurity.Verify(settings.CompanionAccessToken, accessToken);
    }

    private void EnsureBillValidatorStarted(bool force = false)
    {
        if (!force)
        {
            if (_validatorStartTask is { IsCompleted: false })
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _nextValidatorStartAttemptUtc)
            {
                return;
            }
        }

        _nextValidatorStartAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        _validatorStartTask = Task.Run(StartBillValidatorAsync);
    }

    private void EnsureEsp32Started(bool force = false)
    {
        if (!force)
        {
            if (_esp32StartTask is { IsCompleted: false })
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _nextEsp32StartAttemptUtc)
            {
                return;
            }
        }

        _nextEsp32StartAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(20);
        _esp32StartTask = Task.Run(StartEsp32Async);
    }

    private async Task StartBillValidatorAsync()
    {
        await _validatorStartLock.WaitAsync();
        try
        {
            var settings = await GetCompatibleSettingsAsync();
            if (!settings.BillValidatorEnabled || !settings.CashPaymentEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.BillValidatorPortName) || settings.BillValidatorBaudRate <= 0)
            {
                return;
            }

            await _billValidatorGateway.StartAsync(
                settings.BillValidatorPortName,
                settings.BillValidatorBaudRate,
                settings.BillValidatorEscrowMode);
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "Cash",
                Message = "Validatorul NV9 nu a putut fi pornit.",
                Details = ex.Message,
            });
        }
        finally
        {
            _validatorStartLock.Release();
        }
    }

    private async Task StartEsp32Async()
    {
        await _esp32StartLock.WaitAsync();
        try
        {
            var settings = await GetCompatibleSettingsAsync();
            if (!settings.Esp32Enabled)
            {
                return;
            }

            await _esp32Gateway.StartAsync(
                settings.Esp32PortName,
                settings.Esp32BaudRate,
                settings.Esp32AutoDiscover);
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "ESP32",
                Message = "Controllerul ESP32 nu a putut fi pornit.",
                Details = ex.Message,
            });
        }
        finally
        {
            _esp32StartLock.Release();
        }
    }

    private void OnBillValidatorNoteRead(decimal amount) => HandleNoteRead(amount);

    private void OnBillValidatorCreditAccepted(decimal amount) => _ = HandleCreditAcceptedAsync(amount);

    private void OnBillValidatorNoteRejected() => _ = HandleNoteRejectedAsync();

    private void OnEsp32SensorSnapshotReceived(SensorSnapshot snapshot) => _ = HandleEsp32SensorSnapshotAsync(snapshot);

    private void OnEsp32DispenseProgressReceived(decimal dispensedLiters) => _ = HandleEsp32DispenseProgressAsync(dispensedLiters);

    private void OnEsp32DispenseCompleted() => _ = HandleEsp32DispenseCompletedAsync();

    private void OnEsp32PortDetected(string portName) => _ = SafeLogAsync(new DeviceLogEntry
    {
        Category = "ESP32",
        Message = $"ESP32 conectat pe portul {portName}.",
    });

    private void HandleNoteRead(decimal amount)
    {
        try
        {
            var settings = GetCompatibleSettingsAsync().GetAwaiter().GetResult();
            var decision = DecideNoteHandling(settings, amount);

            if (decision.Accept)
            {
                _sync.Wait();
                try
                {
                    NormalizeSessionUnsafe(settings);
                    _session.ActivePaymentMethod = PaymentMethod.Cash;
                    _session.IsCardSelectionBlocked = true;

                    if (_session.CurrentCreditAmount <= 0)
                    {
                        _session.RequestedLiters = 0;
                        _session.TotalAmount = 0;
                    }
                }
                finally
                {
                    _sync.Release();
                }

                if (settings.BillValidatorEscrowMode)
                {
                    _billValidatorGateway.AcceptEscrowAsync().GetAwaiter().GetResult();
                }
            }
            else
            {
                _billValidatorGateway.ReturnInsertedNoteAsync().GetAwaiter().GetResult();
            }

            _ = SafeLogAsync(new DeviceLogEntry
            {
                Category = "Cash",
                Message = decision.Accept
                    ? settings.BillValidatorEscrowMode
                        ? $"Bancnota de {amount:0.00} RON a fost acceptata in escrow."
                        : $"Bancnota de {amount:0.00} RON a fost acceptata pentru auto-stack."
                    : $"Bancnota de {amount:0.00} RON a fost returnata.",
                Details = decision.Reason,
            });
        }
        catch (Exception ex)
        {
            _ = SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "Cash",
                Message = "Nu am putut procesa bancnota citita de validator.",
                Details = ex.Message,
            });
        }
    }

    private async Task HandleCreditAcceptedAsync(decimal amount)
    {
        try
        {
            await AddCreditAsync(amount);
            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "Cash",
                Message = $"Credit adaugat de validator: {amount:0.00} RON.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "Cash",
                Message = "Nu am putut aplica creditul validatorului.",
                Details = ex.Message,
            });
        }
    }

    private async Task ApplyCreditAsync(
        decimal amount,
        MachineSettings settings,
        CancellationToken cancellationToken,
        bool replaceExisting = false)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            NormalizeSessionUnsafe(settings);
            _session.ActivePaymentMethod = PaymentMethod.Cash;
            _session.IsCardSelectionBlocked = true;
            _session.CurrentCreditAmount = replaceExisting
                ? amount
                : _session.CurrentCreditAmount + amount;
            _session.RequestedLiters = settings.PricePerLiter <= 0
                ? 0
                : Math.Round(_session.CurrentCreditAmount / settings.PricePerLiter, 2);
            _session.TotalAmount = _session.CurrentCreditAmount;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task HandleNoteRejectedAsync()
    {
        try
        {
            var settings = await GetCompatibleSettingsAsync();

            await _sync.WaitAsync();
            try
            {
                NormalizeSessionUnsafe(settings);
                if (_session.CurrentCreditAmount <= 0)
                {
                    _session.IsCardSelectionBlocked = false;
                    _session.ActivePaymentMethod = ResolveDefaultPaymentMethod(settings);
                    _session.RequestedLiters = 0;
                    _session.TotalAmount = 0;
                }
            }
            finally
            {
                _sync.Release();
            }

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "Cash",
                Message = "Bancnota a fost respinsa sau a expirat din escrow.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "Cash",
                Message = "Nu am putut reseta sesiunea cash dupa respingerea bancnotei.",
                Details = ex.Message,
            });
        }
    }

    private async Task HandleEsp32SensorSnapshotAsync(SensorSnapshot snapshot)
    {
        await _sync.WaitAsync();
        try
        {
            _sensor.TemperatureCelsius = snapshot.TemperatureCelsius;
            _sensor.HumidityPercent = snapshot.HumidityPercent;
            _sensor.FlowSensorOnline = snapshot.FlowSensorOnline;
            _sensor.PumpOnline = snapshot.PumpOnline;
            _lastRealSensorUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task HandleEsp32DispenseProgressAsync(decimal dispensedLiters)
    {
        await _sync.WaitAsync();
        try
        {
            if (_session.ActivityState == MachineActivityState.Dispensing)
            {
                _session.DispensedLiters = Math.Max(_session.DispensedLiters, Math.Round(dispensedLiters, 3));
                _lastDispenseProgressUtc = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task HandleEsp32DispenseCompletedAsync()
    {
        SaleTransaction? sale = null;
        MachineSettings? settings = null;

        await _sync.WaitAsync();
        try
        {
            if (_session.ActivityState == MachineActivityState.Dispensing)
            {
                _session.DispensedLiters = _session.RequestedLiters;
                _lastDispenseProgressUtc = DateTimeOffset.UtcNow;
                sale = _activeSale;
                settings = _activeDispenseSettings;
            }
        }
        finally
        {
            _sync.Release();
        }

        if (sale is not null && settings is not null && settings.RuntimeMode == RuntimeMode.Production)
        {
            await CompleteDispenseAsync(
                settings,
                sale,
                sale.RequestedLiters,
                $"Dozare confirmata de ESP32: {sale.RequestedLiters:0.##} L / {sale.TotalAmount:0.00} RON.");
        }
    }

    private (bool Accept, string Reason) DecideNoteHandling(MachineSettings settings, decimal amount)
    {
        _sync.Wait();
        try
        {
            NormalizeSessionUnsafe(settings);

            if (!settings.CashPaymentEnabled)
            {
                return (false, "Plata cash este dezactivata.");
            }

            if (!settings.BillValidatorEnabled)
            {
                return (false, "Validatorul de bancnote este dezactivat din setari.");
            }

            if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.OutOfService)
            {
                return (false, "Masina nu poate primi numerar in starea curenta.");
            }

            if (!IsCashChannelEnabled(settings, amount))
            {
                return (false, "Valoarea bancnotei este inhibata din setari.");
            }

            return (true, "Bancnota este permisa.");
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<bool> TrySendDispenseCommandAsync(MachineSettings settings, decimal requestedLiters)
    {
        if (!settings.Esp32Enabled)
        {
            return false;
        }

        try
        {
            await _esp32Gateway.SendDispenseRequestAsync(requestedLiters, settings.PulsesPerLiter);
            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "ESP32",
                Message = $"Cerere de dozare trimisa catre ESP32 pentru {requestedLiters:0.##} L.",
            });
            return true;
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "ESP32",
                Message = "Nu am putut trimite cererea de dozare catre ESP32.",
                Details = ex.Message,
            });
            return false;
        }
    }

    private async Task TrySendSanitationCommandAsync(SanitationRequest request)
    {
        try
        {
            var settings = await GetCompatibleSettingsAsync();
            if (!settings.Esp32Enabled)
            {
                return;
            }

            await _esp32Gateway.SendSanitationAsync(request.Mode, request.Duration, request.PulseOn, request.PulseOff);
            await SafeLogAsync(new DeviceLogEntry
            {
                Category = "ESP32",
                Message = $"Comanda de curatare {request.Mode} a fost trimisa catre ESP32.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = "ESP32",
                Message = "Nu am putut trimite comanda de curatare catre ESP32.",
                Details = ex.Message,
            });
        }
    }

    private async Task SimulateDispenseAsync(MachineSettings settings, SaleTransaction sale)
    {
        if (settings.RuntimeMode != RuntimeMode.Demo)
        {
            return;
        }

        try
        {
            var step = Math.Max(0.05m, Math.Round(sale.RequestedLiters / 18m, 2));

            while (true)
            {
                await Task.Delay(500);

                var completed = false;
                await _sync.WaitAsync();
                try
                {
                    if (DateTimeOffset.UtcNow - _lastDispenseProgressUtc > TimeSpan.FromSeconds(2))
                    {
                        _session.DispensedLiters = Math.Min(sale.RequestedLiters, _session.DispensedLiters + step);
                    }

                    completed = _session.DispensedLiters >= sale.RequestedLiters;
                }
                finally
                {
                    _sync.Release();
                }

                if (completed)
                {
                    break;
                }
            }

            await CompleteDispenseAsync(
                settings,
                sale,
                sale.RequestedLiters,
                $"Dozare demo finalizata: {sale.RequestedLiters:0.##} L / {sale.TotalAmount:0.00} RON.");
        }
        catch (Exception ex)
        {
            await FailActiveDispenseAsync(settings, sale, ex.Message);
        }
    }

    private async Task CompleteDispenseAsync(
        MachineSettings settings,
        SaleTransaction sale,
        decimal dispensedLiters,
        string message,
        CancellationToken cancellationToken = default)
    {
        sale.DispensedLiters = Math.Min(sale.RequestedLiters, Math.Round(dispensedLiters, 3));
        sale.CompletedAtUtc = DateTimeOffset.UtcNow;
        sale.Status = SaleStatus.Completed;

        settings.CurrentStockLiters = Math.Max(0, settings.CurrentStockLiters - sale.DispensedLiters);
        await _settingsRepository.SaveAsync(settings, cancellationToken);
        await _salesRepository.SaveAsync(sale, cancellationToken);

        await SafeLogAsync(new DeviceLogEntry
        {
            Category = "Dispense",
            Message = message,
        }, cancellationToken);
        await _remoteCommandJournal.CompleteAsync(_activeDispenseCommandId, message, cancellationToken);

        await ResetDispenseSessionAsync(settings, cancellationToken);
    }

    private async Task FailActiveDispenseAsync(
        MachineSettings settings,
        SaleTransaction sale,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        sale.Status = SaleStatus.Failed;
        sale.CompletedAtUtc = DateTimeOffset.UtcNow;
        await _salesRepository.SaveAsync(sale, cancellationToken);
        await SafeLogAsync(new DeviceLogEntry
        {
            Severity = LogSeverity.Error,
            Category = "Dispense",
            Message = "Dozarea a esuat.",
            Details = errorMessage,
        }, cancellationToken);
        await _remoteCommandJournal.FailAsync(_activeDispenseCommandId, errorMessage, cancellationToken);

        await ResetDispenseSessionAsync(settings, cancellationToken);
    }

    private async Task ResetDispenseSessionAsync(MachineSettings settings, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            _session = new DispenseSessionState
            {
                ActivityState = MachineActivityState.Ready,
                ActivePaymentMethod = ResolveDefaultPaymentMethod(settings),
            };
            _lastDispenseProgressUtc = DateTimeOffset.MinValue;
            _activeSale = null;
            _activeDispenseSettings = null;
            _activeDispenseCommandId = null;
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task<bool> TryBeginCommandAsync(Guid? commandId, string commandType, object payload, CancellationToken cancellationToken)
    {
        if (commandId is null || commandId.Value == Guid.Empty)
        {
            return true;
        }

        if (!await _remoteCommandJournal.TryBeginAsync(commandId, commandType, payload, cancellationToken))
        {
            return false;
        }

        lock (_executedCommandIds)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var staleCommandId in _executedCommandIds
                         .Where(item => now - item.Value > TimeSpan.FromHours(6))
                         .Select(item => item.Key)
                         .ToList())
            {
                _executedCommandIds.Remove(staleCommandId);
            }

            if (_executedCommandIds.ContainsKey(commandId.Value))
            {
                return false;
            }

            _executedCommandIds[commandId.Value] = now;
            return true;
        }
    }

    private async Task SafeLogAsync(DeviceLogEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await _logRepository.SaveAsync(entry, cancellationToken);
        }
        catch
        {
        }
    }

    private void NormalizeSessionUnsafe(MachineSettings settings)
    {
        if (_session.ActivePaymentMethod is null || !IsPaymentMethodAvailable(settings, _session.ActivePaymentMethod.Value))
        {
            _session.ActivePaymentMethod = ResolveDefaultPaymentMethod(settings);
        }

        if (_session.CurrentCreditAmount > 0)
        {
            _session.IsCardSelectionBlocked = true;
            _session.ActivePaymentMethod = PaymentMethod.Cash;
            _session.RequestedLiters = settings.PricePerLiter <= 0
                ? 0
                : Math.Round(_session.CurrentCreditAmount / settings.PricePerLiter, 2);
            _session.TotalAmount = _session.CurrentCreditAmount;
            return;
        }

        if (_session.IsCardSelectionBlocked)
        {
            _session.ActivePaymentMethod = PaymentMethod.Cash;
        }
    }

    private static bool IsPaymentMethodAvailable(MachineSettings settings, PaymentMethod paymentMethod) =>
        paymentMethod switch
        {
            PaymentMethod.Cash => settings.CashPaymentEnabled,
            PaymentMethod.Card => settings.CardPaymentEnabled,
            _ => false,
        };

    private static PaymentMethod ResolveDefaultPaymentMethod(MachineSettings settings)
    {
        if (settings.CashPaymentEnabled)
        {
            return PaymentMethod.Cash;
        }

        if (settings.CardPaymentEnabled)
        {
            return PaymentMethod.Card;
        }

        return PaymentMethod.Cash;
    }

    private static bool IsCashChannelEnabled(MachineSettings settings, decimal amount)
    {
        var roundedAmount = Math.Round(amount, 2);
        return settings.CashChannels.Any(channel => channel.IsEnabled && Math.Round(channel.Amount, 2) == roundedAmount);
    }

    private void TickSensorSnapshot()
    {
        if (DateTimeOffset.UtcNow - _lastRealSensorUtc <= TimeSpan.FromSeconds(20))
        {
            return;
        }

        var minute = DateTime.UtcNow.TimeOfDay.TotalMinutes;
        _sensor.TemperatureCelsius = 4.2f + (float)(Math.Sin(minute / 5d) * 0.35d);
        _sensor.HumidityPercent = 58f + (float)(Math.Cos(minute / 8d) * 2.5d);
    }

    private async Task<MachineSettings> GetCompatibleSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        var shouldPersist = NormalizeSettings(settings);
        shouldPersist |= ApplyLegacyBillValidatorCompatibility(settings);
        if (shouldPersist)
        {
            await _settingsRepository.SaveAsync(settings, cancellationToken);
        }

        return settings;
    }

    private static bool ApplyLegacyBillValidatorCompatibility(MachineSettings settings)
    {
        if (!settings.BillValidatorEscrowMode)
        {
            return false;
        }

        if (!string.Equals(settings.BillValidatorPortName, "/dev/ttyACM0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (settings.BillValidatorBaudRate != 115200)
        {
            return false;
        }

        settings.BillValidatorEscrowMode = false;
        return true;
    }

    private static bool NormalizeSettings(MachineSettings settings)
    {
        var changed = false;

        if (settings.MachineId == Guid.Empty)
        {
            settings.MachineId = Guid.NewGuid();
            changed = true;
        }

        var normalizedLocalApiBaseUrl = NormalizeBaseUrl(settings.LocalApiBaseUrl, "http://vendomat.local:1326");
        if (!string.Equals(settings.LocalApiBaseUrl, normalizedLocalApiBaseUrl, StringComparison.Ordinal))
        {
            settings.LocalApiBaseUrl = normalizedLocalApiBaseUrl;
            changed = true;
        }

        var normalizedPublicApiBaseUrl = NormalizeBaseUrl(settings.PublicApiBaseUrl, string.Empty);
        if (!string.Equals(settings.PublicApiBaseUrl, normalizedPublicApiBaseUrl, StringComparison.Ordinal))
        {
            settings.PublicApiBaseUrl = normalizedPublicApiBaseUrl;
            changed = true;
        }

        var normalizedCloudApiBaseUrl = NormalizeBaseUrl(settings.CloudApiBaseUrl, "https://vending.dllsoft.ro");
        if (!string.Equals(settings.CloudApiBaseUrl, normalizedCloudApiBaseUrl, StringComparison.Ordinal))
        {
            settings.CloudApiBaseUrl = normalizedCloudApiBaseUrl;
            changed = true;
        }

        var normalizedMachineToken = CompanionAccessTokenSecurity.Normalize(settings.CloudMachineToken);
        if (string.IsNullOrWhiteSpace(normalizedMachineToken))
        {
            normalizedMachineToken = CompanionAccessTokenSecurity.GenerateToken();
        }

        if (!string.Equals(settings.CloudMachineToken, normalizedMachineToken, StringComparison.Ordinal))
        {
            settings.CloudMachineToken = normalizedMachineToken;
            changed = true;
        }

        var normalizedCompanionToken = CompanionAccessTokenSecurity.Normalize(settings.CompanionAccessToken);
        if (!string.Equals(settings.CompanionAccessToken, normalizedCompanionToken, StringComparison.Ordinal))
        {
            settings.CompanionAccessToken = normalizedCompanionToken;
            changed = true;
        }

        var normalizedCashChannels = NormalizeCashChannels(settings.CashChannels);
        if (!CashChannelsEqual(settings.CashChannels, normalizedCashChannels))
        {
            settings.CashChannels = normalizedCashChannels;
            changed = true;
        }

        return changed;
    }

    private static string NormalizeBaseUrl(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized;
    }

    private static List<CashChannelSetting> NormalizeCashChannels(List<CashChannelSetting>? channels)
    {
        var source = channels is { Count: > 0 }
            ? channels
            : new MachineSettings().CashChannels;

        return source
            .Where(channel => channel.Channel > 0)
            .GroupBy(channel => channel.Channel)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var channel = group.First();
                return new CashChannelSetting
                {
                    Channel = channel.Channel,
                    Label = channel.Label,
                    Amount = channel.Amount,
                    IsEnabled = channel.IsEnabled,
                };
            })
            .ToList();
    }

    private static bool CashChannelsEqual(IReadOnlyList<CashChannelSetting>? left, IReadOnlyList<CashChannelSetting>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Channel != right[index].Channel
                || !string.Equals(left[index].Label, right[index].Label, StringComparison.Ordinal)
                || left[index].Amount != right[index].Amount
                || left[index].IsEnabled != right[index].IsEnabled)
            {
                return false;
            }
        }

        return true;
    }

    private static T Clone<T>(T source) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))
        ?? throw new InvalidOperationException("Clone failed.");
}
