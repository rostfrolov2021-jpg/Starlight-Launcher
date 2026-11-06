using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Starlight.Domain;

namespace Starlight.Server;

public sealed class SocialHub(SocialStore store, RelayRooms rooms, NatMediator nat) : Hub
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> GroupsMembers = new();
    private string Id => Context.UserIdentifier ?? throw new HubException("Требуется авторизация.");
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Id); store.Online[Id] = new("В сети"); await NotifyAsync(); await base.OnConnectedAsync();
    }
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        store.Online.TryRemove(Id, out _); rooms.RemoveOwner(Id); await NotifyAsync(); await base.OnDisconnectedAsync(exception);
    }
    public Friend[] Friends()
    {
        var user = store.Get(Id);
        return user.Friends.Select(id => { var u = store.Get(id); var p = store.Online.GetValueOrDefault(id); return new Friend(id, u.Name, p?.Status ?? "Не в сети", p?.Room); })
            .Concat(user.Requests.Where(id => !user.Friends.Contains(id)).Select(id => new Friend(id, store.Get(id).Name, "Входящая заявка · нажмите «Принять»", null, true)))
            .Concat(store.Outgoing(Id).Where(u => !user.Requests.Contains(u.Id)).Select(u => new Friend(u.Id, u.Name, "Заявка отправлена · ожидает ответа", null, false, true))).ToArray();
    }
    public async Task RequestFriend(string name) { var other = await store.RequestAsync(Id, name); await Clients.Groups(Id, other).SendAsync("FriendsChanged"); }
    public SocialUser[] SearchUsers(string prefix) => store.Search(Id, prefix);
    public async Task AcceptFriend(string other) { await store.AcceptAsync(Id, other); await Clients.Groups(Id, other).SendAsync("FriendsChanged"); }
    public async Task RemoveFriend(string other) { await store.RemoveAsync(Id, other); await Clients.Groups(Id, other).SendAsync("FriendsChanged"); }
    public async Task SetPresence(Presence presence)
    {
        if (presence.Status.Length > 100) throw new HubException("Слишком длинный статус.");
        if (presence.Room != null && !rooms.IsOwner(presence.Room, Id)) throw new HubException("Комната не принадлежит вам.");
        store.Online[Id] = presence; await NotifyAsync();
    }
    public async Task SendMessage(string target, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new HubException("Сообщение: 1–4000 символов.");
        var message = new ChatMessage(Id, store.Get(Id).Name, target, text.Trim(), DateTimeOffset.UtcNow);
        if (target.StartsWith("group:", StringComparison.Ordinal))
        {
            if (!GroupsMembers.TryGetValue(target, out var members) || !members.Contains(Id)) throw new HubException("Нет доступа к группе.");
            await Clients.Groups(members.ToArray()).SendAsync("Message", message);
        }
        else
        {
            if (!store.AreFriends(Id, target)) throw new HubException("Сначала добавьте пользователя в друзья.");
            await Clients.Groups(Id, target).SendAsync("Message", message);
        }
    }
    public async Task<string> CreateGroup(string[] friends)
    {
        if (friends.Length is < 1 or > 20 || friends.Any(f => !store.AreFriends(Id, f))) throw new HubException("Выберите до 20 друзей.");
        var id = "group:" + Guid.NewGuid().ToString("N"); var members = new HashSet<string>(friends) { Id }; GroupsMembers[id] = members;
        await Clients.Groups(members.ToArray()).SendAsync("GroupCreated", id); return id;
    }
    public string HostRoom() => rooms.Create(Id);
    public void CloseRoom(string room) { if (!rooms.IsOwner(room, Id)) throw new HubException("Нет доступа."); rooms.RemoveOwner(Id); }
    public async Task OpenTunnel(string room, string stream, string secret)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(secret, "^[A-F0-9]{64}$")) throw new HubException("Неверный ключ соединения.");
        var owner = rooms.Authorize(room, Id, store);
        nat.Allow(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(secret))));
        rooms.Reserve(room, stream, Id); await Clients.Group(owner).SendAsync("OpenTunnel", room, stream, secret);
    }
    public async Task VoiceOffer(string target, string endpoint, string secret)
    {
        if (!store.AreFriends(Id, target) || endpoint.Length > 128 || secret.Length > 128) throw new HubException("Недопустимый вызов.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(secret, "^[A-F0-9]{64}$")) throw new HubException("Неверный ключ вызова.");
        nat.Allow(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(secret))));
        await Clients.Group(target).SendAsync("VoiceOffer", Id, endpoint, secret);
    }
    private Task NotifyAsync() => Clients.Groups(store.Get(Id).Friends.ToArray()).SendAsync("FriendsChanged");
}
