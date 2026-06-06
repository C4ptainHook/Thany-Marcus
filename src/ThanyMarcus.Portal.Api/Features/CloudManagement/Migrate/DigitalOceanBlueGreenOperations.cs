using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;

public sealed class DigitalOceanBlueGreenOperations(IHttpClientFactory httpFactory) : IBlueGreenOperations
{
    public const string HttpClientName = "do-bluegreen";

    public string Provider => "digitalocean";

    public async Task<bool> HasCapacityAsync(string accessToken, CancellationToken ct)
    {
        using var client = Client(accessToken);

        using var accountResp = await client.GetAsync(new Uri("v2/account", UriKind.Relative), ct);
        accountResp.EnsureSuccessStatusCode();
        using var accountDoc = JsonDocument.Parse(await accountResp.Content.ReadAsStringAsync(ct));
        var limit = accountDoc.RootElement.GetProperty("account").GetProperty("droplet_limit").GetInt32();

        using var dropletsResp = await client.GetAsync(new Uri("v2/droplets?per_page=1", UriKind.Relative), ct);
        dropletsResp.EnsureSuccessStatusCode();
        using var dropletsDoc = JsonDocument.Parse(await dropletsResp.Content.ReadAsStringAsync(ct));
        var total = dropletsDoc.RootElement.GetProperty("meta").GetProperty("total").GetInt32();

        return limit > total;
    }

    public async Task<string> FindActiveDropletIdAsync(string accessToken, Cloud cloud, CancellationToken ct)
    {
        using var client = Client(accessToken);
        using var resp = await client.GetAsync(
            new Uri($"v2/droplets?tag_name=cloud-id-{cloud.Id}", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var droplets = doc.RootElement.GetProperty("droplets");
        if (droplets.GetArrayLength() == 0)
            throw new InvalidOperationException($"No active DO droplet tagged cloud-id-{cloud.Id}");
        return droplets[0].GetProperty("id").GetRawText();
    }

    public async Task<string> SnapshotDataVolumeAsync(string accessToken, Cloud cloud, CancellationToken ct)
    {
        using var client = Client(accessToken);
        var volumeId = await FindDataVolumeIdAsync(client, cloud, ct);

        using var resp = await client.PostAsJsonAsync(
            new Uri($"v2/volumes/{volumeId}/snapshots", UriKind.Relative),
            new { name = $"thany-premigrate-{cloud.Id:N}" }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("snapshot").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("DO snapshot response missing id");
    }

    public Task<GreenDroplet> ProvisionGreenAsync(
        string accessToken, Cloud cloud, string snapshotId, string targetVersion, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Green provisioning for cloud {cloud.Id} (target {targetVersion}, snapshot {snapshotId}) " +
            "reuses the Create-saga terraform path; it is not issued via the raw DO API.");

    public async Task<bool> HealthyAsync(string ipv4, CancellationToken ct)
    {
        using var client = httpFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            using var resp = await client.GetAsync(new Uri($"http://{ipv4}/health/live"), ct);
            return (int)resp.StatusCode < 500;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task ReassignReservedIpAsync(string accessToken, Cloud cloud, string dropletId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cloud.VmIp))
            throw new InvalidOperationException("Cloud has no reserved IP to reassign");

        using var client = Client(accessToken);
        using var resp = await client.PostAsJsonAsync(
            new Uri($"v2/reserved_ips/{cloud.VmIp}/actions", UriKind.Relative),
            new { type = "assign", droplet_id = long.Parse(dropletId, System.Globalization.CultureInfo.InvariantCulture) }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DestroyDropletAsync(string accessToken, string dropletId, CancellationToken ct)
    {
        using var client = Client(accessToken);
        using var resp = await client.DeleteAsync(new Uri($"v2/droplets/{dropletId}", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<string> FindDataVolumeIdAsync(HttpClient client, Cloud cloud, CancellationToken ct)
    {
        var name = $"thany-{cloud.Id.ToString("N")[..8]}-data";
        using var resp = await client.GetAsync(
            new Uri($"v2/volumes?name={name}&region={cloud.Region}", UriKind.Relative), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var volumes = doc.RootElement.GetProperty("volumes");
        if (volumes.GetArrayLength() == 0)
            throw new InvalidOperationException($"No DO data volume named '{name}' in {cloud.Region}");
        return volumes[0].GetProperty("id").GetString()
            ?? throw new InvalidOperationException("DO volume response missing id");
    }

    private HttpClient Client(string accessToken)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}
