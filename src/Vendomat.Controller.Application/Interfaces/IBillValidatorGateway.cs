namespace Vendomat.Controller.Application.Interfaces;

public interface IBillValidatorGateway
{
    event Action<decimal>? NoteRead;
    event Action<decimal>? CreditAccepted;
    event Action? NoteRejected;

    Task StartAsync(string portName, int baudRate, bool escrowMode, CancellationToken cancellationToken = default);
    Task ReturnInsertedNoteAsync(CancellationToken cancellationToken = default);
    Task AcceptEscrowAsync(CancellationToken cancellationToken = default);
}
