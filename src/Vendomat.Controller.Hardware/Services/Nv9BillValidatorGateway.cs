using Vendomat.Common.BillValidator;
using Vendomat.Common.SSP;
using Vendomat.Controller.Application.Interfaces;

namespace Vendomat.Controller.Hardware.Services;

public sealed class Nv9BillValidatorGateway : IBillValidatorGateway
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private NV9USB? _validator;
    private Task? _mainLoopTask;
    private bool _stopping;
    private string? _currentPortName;
    private int _currentBaudRate;
    private bool _currentEscrowMode;

    public event Action<decimal>? NoteRead;
    public event Action<decimal>? CreditAccepted;
    public event Action? NoteRejected;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? Faulted;

    public bool IsRunning => _mainLoopTask is { IsCompleted: false };

    public async Task StartAsync(string portName, int baudRate, bool escrowMode, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_validator is not null && (_mainLoopTask is null || _mainLoopTask.IsCompleted))
            {
                await StopCoreAsync(cancellationToken);
            }

            if (ShouldRestart(portName, baudRate, escrowMode))
            {
                await StopCoreAsync(cancellationToken);
            }

            if (_mainLoopTask is { IsCompleted: false })
            {
                StatusChanged?.Invoke($"Validator NV9 ruleaza deja pe {_currentPortName} la {_currentBaudRate} baud.");
                return;
            }

            StatusChanged?.Invoke($"Pornire validator NV9 pe {portName} la {baudRate} baud, escrow={(escrowMode ? "activ" : "inactiv")}.");
            _stopping = false;
            _validator = new NV9USB(portName, baudRate, escrowMode);
            _validator.Validator.NoteAdded += OnCreditAccepted;
            _validator.Validator.ValidatorEvent += OnValidatorEvent;

            _currentPortName = portName;
            _currentBaudRate = baudRate;
            _currentEscrowMode = escrowMode;

            _mainLoopTask = RunMainLoopAsync(_validator, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task ReturnInsertedNoteAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[NV9] Host requested note return");
        _validator?.ReturnNote();
        return Task.CompletedTask;
    }

    public Task AcceptEscrowAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[NV9] Host requested note stack");
        _validator?.AcceptNote();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private bool ShouldRestart(string portName, int baudRate, bool escrowMode)
    {
        if (_validator is null || _mainLoopTask is null)
        {
            return false;
        }

        if (_mainLoopTask.IsCompleted)
        {
            return false;
        }

        return !string.Equals(_currentPortName, portName, StringComparison.OrdinalIgnoreCase)
            || _currentBaudRate != baudRate
            || _currentEscrowMode != escrowMode;
    }

    private async Task RunMainLoopAsync(NV9USB validator, CancellationToken cancellationToken)
    {
        try
        {
            StatusChanged?.Invoke("Bucla validatorului NV9 a pornit.");
            await validator.MainLoop().WaitAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                StatusChanged?.Invoke("Bucla validatorului NV9 s-a oprit.");
                if (!_stopping)
                {
                    Faulted?.Invoke(new InvalidOperationException("Bucla validatorului NV9 s-a oprit neasteptat."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Bucla validatorului NV9 a fost oprita.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NV9] Validator loop failed: {ex}");
            Faulted?.Invoke(ex);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_validator is null)
        {
            _mainLoopTask = null;
            return;
        }

        #pragma warning disable CS8601 // SSP legacy exposes callbacks as non-nullable properties.
        _validator.Validator.NoteAdded -= OnCreditAccepted;
        _validator.Validator.ValidatorEvent -= OnValidatorEvent;
        #pragma warning restore CS8601
        StatusChanged?.Invoke("Oprire validator NV9.");
        _stopping = true;
        _validator.Running = false;

        if (_mainLoopTask is not null)
        {
            try
            {
                await _mainLoopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _validator = null;
        _mainLoopTask = null;
        _currentPortName = null;
        _currentBaudRate = 0;
        _currentEscrowMode = false;
        StatusChanged?.Invoke("Validator NV9 oprit.");
    }

    private void OnCreditAccepted(ValidatorCredit credit)
    {
        StatusChanged?.Invoke($"Validator NV9 a acceptat credit {credit.Amount:0.00}.");
        CreditAccepted?.Invoke(credit.Amount);
    }

    private void OnValidatorEvent(PollResponse type, ValidatorCredit credit)
    {
        switch (type)
        {
            case PollResponse.SSP_POLL_READ_NOTE when credit is not null:
                StatusChanged?.Invoke($"Validator NV9 a citit bancnota {credit.Amount:0.00}.");
                NoteRead?.Invoke(credit.Amount);
                break;

            case PollResponse.SSP_POLL_NOTE_REJECTED:
                StatusChanged?.Invoke("Validator NV9 a respins bancnota.");
                NoteRejected?.Invoke();
                break;
        }
    }
}
