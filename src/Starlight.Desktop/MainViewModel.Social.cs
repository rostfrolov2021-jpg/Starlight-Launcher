using Avalonia.Threading;
using ReactiveUI;
using Starlight.Infrastructure;
using Starlight.Domain;
using System.Collections.ObjectModel;

namespace Starlight.Desktop;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim friendsGate = new(1);
    private readonly SemaphoreSlim socialActionGate = new(1);
    private readonly DispatcherTimer friendsTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer friendSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly SemaphoreSlim friendSearchGate = new(1);
    public ObservableCollection<SocialUser> FriendSearchResults { get; } = [];
    private SocialUser? selectedSearchUser;
    public SocialUser? SelectedSearchUser { get => selectedSearchUser; set { this.RaiseAndSetIfChanged(ref selectedSearchUser, value); } }
    public AsyncCommand AddSearchFriend { get; private set; } = null!;
    private string friendSearchStatus = "Поиск по первым трём буквам имени.";
    public string FriendSearchStatus { get => friendSearchStatus; set => this.RaiseAndSetIfChanged(ref friendSearchStatus, value); }
    private string friendActionStatus = "Добавляйте друзей по имени аккаунта Starlight или ID.";
    public string FriendActionStatus { get => friendActionStatus; set => this.RaiseAndSetIfChanged(ref friendActionStatus, value); }
    public string SocialIdentity => social.Session is { } session ? $"{session.Name} · ID {session.Id}" : "Войдите в аккаунт Starlight";
    public string SocialScope => (social.BaseUri ?? (Uri.TryCreate(SocialUrl, UriKind.Absolute, out var configured) ? configured : null)) is { } uri ? uri.IsLoopback
        ? "Локальный режим: аккаунты доступны только этому компьютеру. Для друзей на разных компьютерах нужен общий сервис друзей."
        : "Сервис друзей: " + uri.GetLeftPart(UriPartial.Authority) : "У вас с другом должен быть один и тот же сервис друзей.";
    public AsyncCommand CopySocialId { get; private set; } = null!;
    public AsyncCommand ConnectOfficialSocial { get; private set; } = null!;
    private void InitializeSocialPanel()
    {
        ConnectOfficialSocial = Command(async ct => { await DiscoverOfficialSocialAsync(true, ct); });
        friendSearchTimer.Tick += (_, _) => { friendSearchTimer.Stop(); _ = SearchFriendsSafelyAsync(); };
        AddSearchFriend = SocialCommand(async ct => {
            var user = SelectedSearchUser ?? throw new InvalidOperationException("Выберите пользователя в результатах поиска.");
            await social.RequestFriendAsync(user.Id, ct); await LoadFriendsAsync(ct);
            FriendActionStatus = "Заявка отправлена: " + user.Name;
        });
        CopySocialId = SocialCommand(async _ => { var id = social.Session?.Id ?? throw new InvalidOperationException("Сначала войдите в аккаунт Starlight."); if (CopyText != null) await CopyText(id); FriendActionStatus = "Ваш ID скопирован."; });
        friendsTimer.Tick += (_, _) => { if (!disposed && social.Connected && friendsGate.CurrentCount > 0) _ = RefreshFriendsSafelyAsync(); };
        friendsTimer.Start();
    }
    private async Task DiscoverOfficialSocialAsync(bool requested, CancellationToken ct)
    {
        if (social.Connected) { if (requested) FriendActionStatus = "Сначала перезапустите лаунчер, чтобы сменить сервис аккаунта."; return; }
        if (!requested && !SocialDiscovery.IsLocal(SocialUrl)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var endpoint = await SocialDiscovery.ResolveAsync(http, timeout.Token);
            if (endpoint == null) { if (requested) FriendActionStatus = "Общий сервис ещё не опубликован владельцем. Сейчас доступен локальный режим."; return; }
            // Keep the shared address even when the host is temporarily down; never create a second local account silently.
            SocialUrl = endpoint.AbsoluteUri; linkedService = SocialUrl;
            this.RaisePropertyChanged(nameof(SocialUrl)); this.RaisePropertyChanged(nameof(SocialScope));
            FriendActionStatus = "Выбран общий сервис друзей. Войдите или зарегистрируйтесь в нём. Локальные аккаунты остаются на этом ПК.";
            await SaveSettingsAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (requested) FriendActionStatus = "Не удалось получить адрес общего сервиса: " + SocialClient.FriendlyError(e);
        }
    }
    private async Task SearchFriendsSafelyAsync()
    {
        var query = FriendName.Trim();
        try
        {
            await friendSearchGate.WaitAsync(lifetime.Token);
            try
            {
                if (disposed || query != FriendName.Trim()) return;
                if (!social.Connected) { FriendSearchStatus = "Войдите в Starlight, чтобы искать друзей."; return; }
                FriendSearchStatus = "Поиск…";
                var found = await social.SearchUsersAsync(query, lifetime.Token);
                if (query != FriendName.Trim()) return;
                FriendSearchResults.Clear(); foreach (var user in found) FriendSearchResults.Add(user);
                FriendSearchStatus = found.Length > 0 ? "Выберите пользователя и отправьте заявку." : "Совпадений нет в этом сервисе друзей.";
            }
            finally { friendSearchGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { FriendSearchStatus = SocialClient.FriendlyError(error); }
    }
    private AsyncCommand SocialCommand(Func<CancellationToken, Task> action) => new(async () => {
        if (disposed || !await socialActionGate.WaitAsync(0)) return;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { FriendActionStatus = SocialClient.FriendlyError(error); Status = FriendActionStatus; }
        finally { socialActionGate.Release(); }
    });
    private async Task RefreshFriendsSafelyAsync()
    {
        try { if (!disposed && social.Connected) await LoadFriendsAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { FriendActionStatus = SocialClient.FriendlyError(error); }
        this.RaisePropertyChanged(nameof(SocialIdentity)); this.RaisePropertyChanged(nameof(SocialScope));
    }
}
