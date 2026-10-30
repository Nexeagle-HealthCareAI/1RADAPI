using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace _1RadAPI.Live;

/// <summary>
/// Push channel that replaces the dashboards' polling (Oct 2026). A connected browser
/// joins its centre's group and receives <c>changed(topics)</c> — e.g.
/// ["appointments","approvals"] — whenever that centre's data changes; it then
/// re-fetches exactly what it shows. Nothing patient-identifying travels on this
/// channel, only topic names.
///
/// Endpoint: /api/v1/live (under /api/ so the existing nginx / Caddy proxy rules apply).
/// The browser passes its access token as ?access_token= (WebSockets can't send headers).
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    private readonly LiveConnectionRegistry _registry;

    public LiveHub(LiveConnectionRegistry registry) => _registry = registry;

    public static string GroupFor(Guid hospitalId) => $"centre:{hospitalId:D}";

    public override async Task OnConnectedAsync()
    {
        if (Guid.TryParse(Context.User?.FindFirst("cid")?.Value, out var cid) && cid != Guid.Empty)
        {
            Context.Items["cid"] = cid;
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(cid));
            _registry.Add(cid);
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("cid", out var v) && v is Guid cid) _registry.Remove(cid);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>How many browsers on THIS instance watch each centre (the watcher only checks those).</summary>
public sealed class LiveConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public void Add(Guid hospitalId) => _counts.AddOrUpdate(hospitalId, 1, (_, c) => c + 1);

    public void Remove(Guid hospitalId)
    {
        while (_counts.TryGetValue(hospitalId, out var c))
        {
            if (c <= 1)
            {
                if (_counts.TryRemove(new KeyValuePair<Guid, int>(hospitalId, c))) return;
            }
            else if (_counts.TryUpdate(hospitalId, c - 1, c)) return;
        }
    }

    public IReadOnlyCollection<Guid> ActiveCentres() => _counts.Keys.ToList();
}
