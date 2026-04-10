using Vendomat.Common.BillValidator;
using Vendomat.Common.SSP;
using Vendomat.Controller.Application.Interfaces;

namespace Vendomat.Controller.Hardware.Services;

public sealed class Nv9BillValidatorGateway : IBillValidatorGateway
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private NV9USB? _validator;
    private Task? _mainLoopTask;
    private string? _currentPortName;
    private int _currentBaudRate;
    private bool _currentEscrowMode;

    public event Action<decimal>? NoteRead;
    public event Action<decimal>? CreditAccepted;
    public event Action? NoteRejected;

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
                return;
            }

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
            await validator.MainLoop().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NV9] Validator loop failed: {ex}");
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_validator is null)
        {
            _mainLoopTask = null;
            return;
        }

        _validator.Validator.NoteAdded -= OnCreditAccepted;
        _validator.Validator.ValidatorEvent -= OnValidatorEvent;
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
    }

    private void OnCreditAccepted(ValidatorCredit credit)
    {
        CreditAccepted?.Invoke(credit.Amount);
    }

    private void OnValidatorEvent(PollResponse type, ValidatorCredit credit)
    {
        switch (type)
        {
            case PollResponse.SSP_POLL_READ_NOTE when credit is not null:
                NoteRead?.Invoke(credit.Amount);
                break;

            case PollResponse.SSP_POLL_NOTE_REJECTED:
                NoteRejected?.Invoke();
                break;
        }
    }
}
