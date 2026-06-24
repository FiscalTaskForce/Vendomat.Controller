using System.Text.Json;
using Android.Util;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Sales;
using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Tablet.Services;

public sealed class MachineRuntimeService : IMachineRuntimeService
{
    private const string RemoteCreditCommandType = "remote-credit";
    private const string DispenseCommandType = "dispense";
    private const string PrimingCommandType = "priming";
    private const string SanitationCommandType = "sanitation";
    private const string Esp32FirmwareUpdateCommandType = "esp32-firmware-update";
    private const string ValidatorLogTag = "VendomatValidator";

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
    private TaskCompletionSource<bool>? _activePrimingCompletion;
    private SanitationRecord? _activeSanitation;
    private Guid? _activeSanitationCommandId;

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
        _billValidatorGateway.StatusChanged += OnBillValidatorStatusChanged;
        _billValidatorGateway.Faulted += OnBillValidatorFaulted;
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

            // Broadcast the LAN IPv4 address rather than the mDNS hostname so paired clients
            // can reach the local API by IP; only the outbound copy is rewritten, not the
            // persisted (user-configured) setting.
            var outboundSettings = Clone(settings);
            outboundSettings.LocalApiBaseUrl = LanAddressResolver.ResolveBaseUrl(outboundSettings.LocalApiBaseUrl);

            return new MachineStatusSnapshot
            {
                Settings = outboundSettings,
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
        var recentLogs = (await _logRepository.GetRecentAsync(20, cancellationToken))
            .OrderByDescending(item => item.CreatedAtUtc)
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
            RecentLogs = recentLogs,
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
            Category = LogCategories.Settings,
            Message = "Setarile controllerului au fost actualizate.",
        }, cancellationToken);
    }

    public async Task SetPaymentMethodAsync(PaymentMethod paymentMethod, CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.Priming)
            {
                throw new InvalidOperationException(DispenseMessages.PaymentMethodLockedDuringOperation);
            }

            switch (paymentMethod)
            {
                case PaymentMethod.Cash:
                    if (!settings.CashPaymentEnabled)
                    {
                        throw new InvalidOperationException(DispenseMessages.CashDisabled);
                    }

                    _session.ActivePaymentMethod = PaymentMethod.Cash;

                    if (_session.CurrentCreditAmount > 0)
                    {
                        _session.IsCardSelectionBlocked = true;
                        _session.RequestedLiters = CalculateLitersFromCredit(_session.CurrentCreditAmount, settings.PricePerLiter);
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
                        throw new InvalidOperationException(DispenseMessages.CardDisabled);
                    }

                    if (_session.IsCardSelectionBlocked || _session.CurrentCreditAmount > 0)
                    {
                        throw new InvalidOperationException(DispenseMessages.CashInProgressFinishFirst);
                    }

                    _session.ActivePaymentMethod = PaymentMethod.Card;
                    break;

                default:
                    throw new InvalidOperationException(DispenseMessages.PaymentMethodUnsupported);
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
            throw new InvalidOperationException(DispenseMessages.CardDisabled);
        }

        var sanitizedLiters = Math.Max(0, Math.Round(liters, 2));

        await _sync.WaitAsync(cancellationToken);
        try
        {
            NormalizeSessionUnsafe(settings);

            if (_session.IsCardSelectionBlocked || _session.CurrentCreditAmount > 0)
            {
                throw new InvalidOperationException(DispenseMessages.CardUnavailableCashSession);
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
                throw new InvalidOperationException(DispenseMessages.CreditCannotBeNegative);
            }

            var settings = await GetCompatibleSettingsAsync(cancellationToken);
            await ApplyCreditAsync(amount, settings, cancellationToken, replaceExisting: true);

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.RemoteCredit,
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
                throw new InvalidOperationException(DispenseMessages.CashDisabled);
            }

            if (command.PaymentMethod == PaymentMethod.Card && !settings.CardPaymentEnabled)
            {
                throw new InvalidOperationException(DispenseMessages.CardDisabled);
            }

            if (requestedLiters <= 0)
            {
                throw new InvalidOperationException(DispenseMessages.SelectQuantityFirst);
            }

            if (command.PaymentMethod == PaymentMethod.Cash && command.CreditAmount < requestedLiters * settings.PricePerLiter)
            {
                throw new InvalidOperationException(DispenseMessages.InsufficientCredit);
            }

            if (settings.RuntimeMode == RuntimeMode.Production && !settings.Esp32Enabled)
            {
                throw new InvalidOperationException(DispenseMessages.DispenseRequiresEsp32);
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
                if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.Priming)
                {
                    throw new InvalidOperationException(DispenseMessages.MachineBusy);
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
                    throw new InvalidOperationException(DispenseMessages.Esp32CommandFailed);
                }

                return;
            }

            RunObserved(() => SimulateDispenseAsync(settings, sale), "simulare dozare");
        }
        catch (Exception ex)
        {
            await _remoteCommandJournal.FailAsync(command.CommandId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task StopDispenseAsync(CancellationToken cancellationToken = default)
    {
        SaleTransaction? sale;
        MachineSettings? settings;
        decimal dispensedLiters;
        decimal currentCreditAmount;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_session.ActivityState != MachineActivityState.Dispensing)
            {
                return;
            }

            sale = _activeSale;
            settings = _activeDispenseSettings;
            dispensedLiters = _session.DispensedLiters;
            currentCreditAmount = _session.CurrentCreditAmount;
            _session.ActivityState = MachineActivityState.Ready;
        }
        finally
        {
            _sync.Release();
        }

        try
        {
            await _esp32Gateway.StopDispenseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Esp32,
                Message = "Comanda de oprire dozare nu a putut fi confirmata de ESP32.",
                Details = ex.Message,
            }, cancellationToken);
        }

        if (sale is not null && settings is not null)
        {
            await CancelActiveDispenseAsync(
                settings,
                sale,
                dispensedLiters,
                currentCreditAmount,
                $"Dozare oprita manual: {dispensedLiters:0.###} L din {sale.RequestedLiters:0.##} L.",
                cancellationToken);
        }
    }

    public async Task RunPrimingAsync(PrimingRequest request, CancellationToken cancellationToken = default)
    {
        if (!await TryBeginCommandAsync(request.CommandId, PrimingCommandType, request, cancellationToken))
        {
            return;
        }

        TaskCompletionSource<bool>? completion = null;

        try
        {
            var settings = await GetCompatibleSettingsAsync(cancellationToken);
            var targetLiters = Math.Round(request.TargetLiters, 3);
            if (targetLiters is < 0.05m or > 1m)
            {
                throw new InvalidOperationException(DispenseMessages.PrimingVolumeRange);
            }

            if (settings.RuntimeMode == RuntimeMode.Production && !settings.Esp32Enabled)
            {
                throw new InvalidOperationException(DispenseMessages.PrimingRequiresEsp32);
            }

            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.Priming)
                {
                    throw new InvalidOperationException(DispenseMessages.MachineBusy);
                }

                _session.ActivityState = MachineActivityState.Priming;
                _session.ActivePaymentMethod = null;
                _session.RequestedLiters = targetLiters;
                _session.DispensedLiters = 0;
                _session.TotalAmount = 0;
                _session.IsCardSelectionBlocked = false;
                _session.IsRemoteOperation = request.CommandId is not null && request.CommandId.Value != Guid.Empty;
                _session.OperationMessage = _session.IsRemoteOperation
                    ? "Se executa o operatie pornita din aplicatia companion."
                    : string.Empty;
                _lastDispenseProgressUtc = DateTimeOffset.MinValue;
                _activePrimingCompletion = completion;
            }
            finally
            {
                _sync.Release();
            }

            var completed = false;
            var timeout = request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : request.Timeout;

            if (settings.RuntimeMode == RuntimeMode.Production)
            {
                EnsureEsp32Started();
                if (!await TrySendPrimingCommandAsync(settings, targetLiters, cancellationToken))
                {
                    throw new InvalidOperationException(DispenseMessages.PrimingCommandFailed);
                }

                completed = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)) == completion.Task;
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                await _sync.WaitAsync(cancellationToken);
                try
                {
                    if (ReferenceEquals(_activePrimingCompletion, completion))
                    {
                        _session.DispensedLiters = targetLiters;
                    }
                }
                finally
                {
                    _sync.Release();
                }

                completed = true;
            }

            if (!completed)
            {
                await TryStopEsp32PumpAsync("Amorsarea a depasit timeout-ul si pompa a fost oprita.", cancellationToken);
                throw new TimeoutException("Amorsarea nu a detectat volumul tinta in timpul configurat.");
            }

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Priming,
                Message = $"Amorsare finalizata pentru {targetLiters * 1000m:0} ml.",
            }, cancellationToken);

            await _remoteCommandJournal.CompleteAsync(request.CommandId, $"Amorsare {targetLiters * 1000m:0} ml finalizata.", cancellationToken);
        }
        catch (Exception ex)
        {
            if (completion is not null)
            {
                await TryStopEsp32PumpAsync("Amorsarea a fost oprita dupa eroare.", cancellationToken);
            }

            await _remoteCommandJournal.FailAsync(request.CommandId, ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            if (completion is not null)
            {
                await ResetPrimingSessionAsync(completion, cancellationToken);
            }
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
            var isRemoteOperation = request.CommandId is not null && request.CommandId.Value != Guid.Empty;
            var activeSanitation = new SanitationRecord
            {
                MachineId = settings.MachineId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Duration = TimeSpan.Zero,
                Mode = request.Mode,
                PulseOn = request.PulseOn,
                PulseOff = request.PulseOff,
                Notes = isRemoteOperation
                    ? "Pornit din aplicatia companion. Oprit manual."
                    : "Pornit din interfata locala a controllerului. Oprit manual.",
            };

            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.Priming)
                {
                    throw new InvalidOperationException(DispenseMessages.SanitationBlockedDuringOperation);
                }

                _session.ActivityState = MachineActivityState.Cleaning;
                _session.ActivePaymentMethod = null;
                _session.RequestedLiters = 0;
                _session.DispensedLiters = 0;
                _session.TotalAmount = 0;
                _session.IsCardSelectionBlocked = false;
                _session.IsRemoteOperation = isRemoteOperation;
                _session.OperationMessage = isRemoteOperation
                    ? "Se executa o operatie pornita din aplicatia companion."
                    : string.Empty;
                _activeSanitation = activeSanitation;
                _activeSanitationCommandId = request.CommandId;
            }
            finally
            {
                _sync.Release();
            }

            EnsureEsp32Started();
            RunObserved(() => TrySendSanitationCommandAsync(request), "trimitere comanda curatare");

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Sanitation,
                Message = $"Curatare pornita in mod {request.Mode}. Oprirea se face manual.",
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            await _remoteCommandJournal.FailAsync(request.CommandId, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task StopSanitationAsync(Guid? commandId = null, CancellationToken cancellationToken = default)
    {
        if (!await TryBeginCommandAsync(commandId, CloudCommandTypes.StopSanitation, new { }, cancellationToken))
        {
            return;
        }

        SanitationRecord? sanitation;
        Guid? activeCommandId;
        var settings = await GetCompatibleSettingsAsync(cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_session.ActivityState != MachineActivityState.Cleaning)
            {
                await _remoteCommandJournal.CompleteAsync(commandId, "Nu exista curatare activa.", cancellationToken);
                return;
            }

            sanitation = _activeSanitation;
            activeCommandId = _activeSanitationCommandId;
            if (sanitation is not null)
            {
                sanitation.Duration = DateTimeOffset.UtcNow - sanitation.StartedAtUtc;
            }

            _session.ActivityState = MachineActivityState.Ready;
            _session.ActivePaymentMethod = ResolveDefaultPaymentMethod(settings);
            _session.IsRemoteOperation = false;
            _session.OperationMessage = string.Empty;
            _activeSanitation = null;
            _activeSanitationCommandId = null;
        }
        finally
        {
            _sync.Release();
        }

        try
        {
            await _esp32Gateway.StopDispenseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Esp32,
                Message = "Comanda de oprire curatare nu a putut fi confirmata de ESP32.",
                Details = ex.Message,
            }, cancellationToken);
        }

        if (sanitation is not null)
        {
            await _sanitationRepository.SaveAsync(sanitation, cancellationToken);
        }

        var message = sanitation is null
            ? "Curatare oprita manual."
            : $"Curatare {sanitation.Mode} oprita manual dupa {sanitation.Duration.TotalSeconds:0}s.";
        await SafeLogAsync(new DeviceLogEntry
        {
            Category = LogCategories.Sanitation,
            Message = message,
        }, cancellationToken);
        await _remoteCommandJournal.CompleteAsync(activeCommandId, message, cancellationToken);
        if (commandId != activeCommandId)
        {
            await _remoteCommandJournal.CompleteAsync(commandId, message, cancellationToken);
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
                throw new InvalidOperationException(DispenseMessages.FirmwareRequestMissing);
            }

            if (string.IsNullOrWhiteSpace(request.FirmwareUrl))
            {
                throw new InvalidOperationException(DispenseMessages.FirmwareUrlRequired);
            }

            var settings = await GetCompatibleSettingsAsync(cancellationToken);
            if (!settings.Esp32Enabled)
            {
                throw new InvalidOperationException(DispenseMessages.Esp32Disabled);
            }

            EnsureEsp32Started(force: true);
            await _esp32Gateway.SendFirmwareUpdateAsync(request, cancellationToken);

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Esp32,
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
            Category = LogCategories.Pairing,
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
            if (_billValidatorGateway.IsRunning)
            {
                return;
            }

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
                await _billValidatorGateway.StopAsync();
                await SafeLogAsync(new DeviceLogEntry
                {
                    Category = LogCategories.Validator,
                    Message = "Validator NV9 oprit deoarece plata cash sau validatorul este dezactivat.",
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.BillValidatorPortName) || settings.BillValidatorBaudRate <= 0)
            {
                await _billValidatorGateway.StopAsync();
                await SafeLogAsync(new DeviceLogEntry
                {
                    Severity = LogSeverity.Warning,
                    Category = LogCategories.Validator,
                    Message = "Validator NV9 neporinit: port sau baud rate invalid.",
                    Details = $"Port='{settings.BillValidatorPortName}', baud={settings.BillValidatorBaudRate}.",
                });
                return;
            }

            await _billValidatorGateway.StartAsync(
                settings.BillValidatorPortName,
                settings.BillValidatorBaudRate,
                settings.BillValidatorEscrowMode);

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Validator,
                Message = $"Validator NV9 pornit pe {settings.BillValidatorPortName} la {settings.BillValidatorBaudRate} baud.",
                Details = $"Escrow={(settings.BillValidatorEscrowMode ? "activ" : "inactiv")}; cash={(settings.CashPaymentEnabled ? "activ" : "inactiv")}.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Validator,
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
                Category = LogCategories.Esp32,
                Message = "Controllerul ESP32 nu a putut fi pornit.",
                Details = ex.Message,
            });
        }
        finally
        {
            _esp32StartLock.Release();
        }
    }

    private void OnBillValidatorNoteRead(decimal amount) => _ = HandleNoteReadAsync(amount);

    private void OnBillValidatorCreditAccepted(decimal amount) => _ = HandleCreditAcceptedAsync(amount);

    private void OnBillValidatorNoteRejected() => _ = HandleNoteRejectedAsync();

    private void OnBillValidatorStatusChanged(string message)
    {
        Log.Info(ValidatorLogTag, message);

        _ = SafeLogAsync(new DeviceLogEntry
        {
            Category = LogCategories.Validator,
            Message = message,
        });
    }

    private void OnBillValidatorFaulted(Exception exception)
    {
        _nextValidatorStartAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        _validatorStartTask = null;

        Log.Warn(ValidatorLogTag, exception.ToString());

        _ = SafeLogAsync(new DeviceLogEntry
        {
            Severity = LogSeverity.Warning,
            Category = LogCategories.Validator,
            Message = "Bucla validatorului NV9 a cazut; se va incerca reconectarea.",
            Details = exception.ToString(),
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            EnsureBillValidatorStarted(force: true);
        });
    }

    private void OnEsp32SensorSnapshotReceived(SensorSnapshot snapshot) => _ = HandleEsp32SensorSnapshotAsync(snapshot);

    private void OnEsp32DispenseProgressReceived(decimal dispensedLiters) => _ = HandleEsp32DispenseProgressAsync(dispensedLiters);

    private void OnEsp32DispenseCompleted() => _ = HandleEsp32DispenseCompletedAsync();

    private void OnEsp32PortDetected(string portName) => _ = SafeLogAsync(new DeviceLogEntry
    {
        Category = LogCategories.Esp32,
        Message = $"ESP32 conectat pe portul {portName}.",
    });

    private async Task HandleNoteReadAsync(decimal amount)
    {
        try
        {
            var settings = await GetCompatibleSettingsAsync();
            var decision = await DecideNoteHandlingAsync(settings, amount);

            if (decision.Accept)
            {
                await _sync.WaitAsync();
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
                    await _billValidatorGateway.AcceptEscrowAsync();
                }
            }
            else
            {
                await _billValidatorGateway.ReturnInsertedNoteAsync();
            }

            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Cash,
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
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Cash,
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
                Category = LogCategories.Cash,
                Message = $"Credit adaugat de validator: {amount:0.00} RON.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Cash,
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
            _session.RequestedLiters = CalculateLitersFromCredit(_session.CurrentCreditAmount, settings.PricePerLiter);
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
                Category = LogCategories.Cash,
                Message = "Bancnota a fost respinsa sau a expirat din escrow.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Cash,
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
            if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Priming)
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
        TaskCompletionSource<bool>? primingCompletion = null;

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
            else if (_session.ActivityState == MachineActivityState.Priming)
            {
                _session.DispensedLiters = _session.RequestedLiters;
                _lastDispenseProgressUtc = DateTimeOffset.UtcNow;
                primingCompletion = _activePrimingCompletion;
            }
        }
        finally
        {
            _sync.Release();
        }

        primingCompletion?.TrySetResult(true);

        if (sale is not null && settings is not null && settings.RuntimeMode == RuntimeMode.Production)
        {
            await CompleteDispenseAsync(
                settings,
                sale,
                sale.RequestedLiters,
                $"Dozare confirmata de ESP32: {sale.RequestedLiters:0.##} L / {sale.TotalAmount:0.00} RON.");
        }
    }

    private async Task<(bool Accept, string Reason)> DecideNoteHandlingAsync(MachineSettings settings, decimal amount)
    {
        await _sync.WaitAsync();
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

            if (_session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.Priming or MachineActivityState.OutOfService)
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
                Category = LogCategories.Esp32,
                Message = $"Cerere de dozare trimisa catre ESP32 pentru {requestedLiters:0.##} L.",
            });
            return true;
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Esp32,
                Message = "Nu am putut trimite cererea de dozare catre ESP32.",
                Details = ex.Message,
            });
            return false;
        }
    }

    private async Task<bool> TrySendPrimingCommandAsync(MachineSettings settings, decimal targetLiters, CancellationToken cancellationToken)
    {
        try
        {
            await _esp32Gateway.SendDispenseRequestAsync(targetLiters, settings.PulsesPerLiter, cancellationToken);
            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Esp32,
                Message = $"Comanda de amorsare trimisa catre ESP32 pentru {targetLiters * 1000m:0} ml.",
            }, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Esp32,
                Message = "Nu am putut trimite comanda de amorsare catre ESP32.",
                Details = ex.Message,
            }, cancellationToken);
            return false;
        }
    }

    private async Task TryStopEsp32PumpAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _esp32Gateway.StopDispenseAsync(cancellationToken);
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Priming,
                Message = reason,
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Error,
                Category = LogCategories.Priming,
                Message = "Nu am putut opri pompa dupa amorsare.",
                Details = ex.Message,
            }, cancellationToken);
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

            var effectiveDuration = request.Duration > TimeSpan.Zero
                ? request.Duration
                : TimeSpan.FromDays(1);
            await _esp32Gateway.SendSanitationAsync(request.Mode, effectiveDuration, request.PulseOn, request.PulseOff);
            await SafeLogAsync(new DeviceLogEntry
            {
                Category = LogCategories.Esp32,
                Message = $"Comanda de curatare {request.Mode} a fost trimisa catre ESP32.",
            });
        }
        catch (Exception ex)
        {
            await SafeLogAsync(new DeviceLogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategories.Esp32,
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
                var stopped = false;
                await _sync.WaitAsync();
                try
                {
                    stopped = _session.ActivityState != MachineActivityState.Dispensing;
                    if (!stopped && DateTimeOffset.UtcNow - _lastDispenseProgressUtc > TimeSpan.FromSeconds(2))
                    {
                        _session.DispensedLiters = Math.Min(sale.RequestedLiters, _session.DispensedLiters + step);
                    }

                    completed = !stopped && _session.DispensedLiters >= sale.RequestedLiters;
                }
                finally
                {
                    _sync.Release();
                }

                if (stopped)
                {
                    return;
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
            Category = LogCategories.Dispense,
            Message = message,
        }, cancellationToken);
        await _remoteCommandJournal.CompleteAsync(_activeDispenseCommandId, message, cancellationToken);

        await ResetDispenseSessionAsync(settings, cancellationToken);
    }

    private async Task CancelActiveDispenseAsync(
        MachineSettings settings,
        SaleTransaction sale,
        decimal dispensedLiters,
        decimal currentCreditAmount,
        string message,
        CancellationToken cancellationToken = default)
    {
        var settlement = SaleMath.ComputeCancellation(
            sale.PaymentMethod,
            sale.RequestedLiters,
            dispensedLiters,
            currentCreditAmount,
            sale.PricePerLiter);

        sale.DispensedLiters = settlement.BilledLiters;
        sale.CompletedAtUtc = DateTimeOffset.UtcNow;
        sale.Status = SaleStatus.Cancelled;
        sale.TotalAmount = settlement.BilledAmount;

        var remainingCredit = settlement.RemainingCredit;
        var remainingLiters = settlement.RemainingLiters;
        var remainingTotal = settlement.RemainingTotal;

        settings.CurrentStockLiters = Math.Max(0, settings.CurrentStockLiters - sale.DispensedLiters);
        await _settingsRepository.SaveAsync(settings, cancellationToken);
        await _salesRepository.SaveAsync(sale, cancellationToken);

        await SafeLogAsync(new DeviceLogEntry
        {
            Severity = LogSeverity.Warning,
            Category = LogCategories.Dispense,
            Message = message,
        }, cancellationToken);
        await _remoteCommandJournal.FailAsync(_activeDispenseCommandId, message, cancellationToken);

        await ResetDispenseSessionAsync(
            settings,
            new DispenseSessionState
            {
                ActivityState = MachineActivityState.Ready,
                ActivePaymentMethod = sale.PaymentMethod,
                RequestedLiters = remainingLiters,
                CurrentCreditAmount = remainingCredit,
                TotalAmount = remainingTotal,
                IsCardSelectionBlocked = remainingCredit > 0,
            },
            cancellationToken);
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
            Category = LogCategories.Dispense,
            Message = "Dozarea a esuat.",
            Details = errorMessage,
        }, cancellationToken);
        await _remoteCommandJournal.FailAsync(_activeDispenseCommandId, errorMessage, cancellationToken);

        await ResetDispenseSessionAsync(settings, cancellationToken);
    }

    private Task ResetDispenseSessionAsync(MachineSettings settings, CancellationToken cancellationToken = default) =>
        ResetDispenseSessionAsync(
            settings,
            new DispenseSessionState
            {
                ActivityState = MachineActivityState.Ready,
                ActivePaymentMethod = ResolveDefaultPaymentMethod(settings),
            },
            cancellationToken);

    private async Task ResetDispenseSessionAsync(
        MachineSettings settings,
        DispenseSessionState nextSession,
        CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            _session = nextSession;
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

    private async Task ResetPrimingSessionAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken = default)
    {
        var settings = await GetCompatibleSettingsAsync(cancellationToken);

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (ReferenceEquals(_activePrimingCompletion, completion))
            {
                _activePrimingCompletion = null;
            }

            if (_session.ActivityState == MachineActivityState.Priming)
            {
                _session.ActivityState = MachineActivityState.Ready;
                _session.ActivePaymentMethod = ResolveDefaultPaymentMethod(settings);
                _session.RequestedLiters = 0;
                _session.DispensedLiters = 0;
                _session.TotalAmount = 0;
                _session.IsRemoteOperation = false;
                _session.OperationMessage = string.Empty;
            }
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

    /// <summary>
    /// Runs a background operation detached from the caller, but logs any escaped exception
    /// instead of leaving the faulted task unobserved.
    /// </summary>
    private void RunObserved(Func<Task> operation, string context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                await SafeLogAsync(new DeviceLogEntry
                {
                    Severity = LogSeverity.Error,
                    Category = LogCategories.Background,
                    Message = $"Operatie de fundal esuata: {context}.",
                    Details = ex.ToString(),
                });
            }
        }, CancellationToken.None);
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
            _session.RequestedLiters = CalculateLitersFromCredit(_session.CurrentCreditAmount, settings.PricePerLiter);
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

    private static decimal CalculateLitersFromCredit(decimal creditAmount, decimal pricePerLiter) =>
        SaleMath.LitersFromCredit(creditAmount, pricePerLiter);

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
