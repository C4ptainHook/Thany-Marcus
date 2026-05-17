using NodaTime;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public sealed class BootstrapState
{
    private readonly Lock gate = new();
    private string status = CloudAdminHealthResponse.RegistrationPending;
    private Instant? registeredAt;
    private int inFlight;

    public string RegistrationStatus
    {
        get { lock (gate) return status; }
        set { lock (gate) status = value; }
    }

    public Instant? RegisteredAt
    {
        get { lock (gate) return registeredAt; }
        set { lock (gate) registeredAt = value; }
    }

    public bool TryBeginRegistration() =>
        Interlocked.CompareExchange(ref inFlight, 1, 0) == 0;

    public void EndRegistration() =>
        Volatile.Write(ref inFlight, 0);
}
