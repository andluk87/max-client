using System.Text;
using System.Text.Json.Nodes;
using QRCoder;
namespace MaxClient;
internal sealed class MainWindow : Form
{
    private readonly ListBox chats = new() { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 68, BorderStyle = BorderStyle.None };
    private readonly TextBox filter = new() { Dock = DockStyle.Top, PlaceholderText = "Поиск чатов", BorderStyle = BorderStyle.FixedSingle };
    private readonly RichTextBox history = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None, DetectUrls = false };
    private readonly TextBox input = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 4000 };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 30, Text = "Не подключён", Padding = new Padding(10, 5, 0, 0), AutoEllipsis = true };
    private readonly Label heading = new() { Dock = DockStyle.Top, Height = 48, Text = "Выберите чат", Padding = new Padding(12, 12, 0, 0), Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoEllipsis = true };
    private readonly Button send = new() { Text = "Отправить\nCtrl+Enter", Dock = DockStyle.Right, Width = 115, Enabled = false };
    private readonly Button connect = new() { Text = "Подключиться / QR", AutoSize = true };
    private readonly Button cancel = new() { Text = "Отмена", AutoSize = true, Enabled = false };
    private readonly Button refresh = new() { Text = "Обновить", AutoSize = true, Enabled = false };
    private readonly Button older = new() { Text = "Ранее", AutoSize = true, Enabled = false };
    private readonly Button logout = new() { Text = "Удалить сессию", AutoSize = true };
    private readonly PictureBox qr = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Panel qrPanel = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.White, Padding = new Padding(40) };
    private readonly Label qrHint = new() { Dock = DockStyle.Bottom, Height = 65, TextAlign = ContentAlignment.MiddleCenter };
    private readonly List<Chat> allChats = [];
    private readonly Dictionary<string, string> names = [];
    private readonly Dictionary<string, Message> messages = [];
    private readonly Dictionary<long, string> drafts = [];
    private readonly System.Windows.Forms.Timer updates = new() { Interval = 15000 };
    private readonly SemaphoreSlim historyGate = new(1, 1);
    private CancellationTokenSource operation = new();
    private MaxConnection? connection;
    private TaskCompletionSource<string>? qrToken;
    private bool authorized, refreshing, closing, renderingChats, connecting, sending;
    private long? selectedChat;
    private string ownId = "";
    private string deviceId = Guid.NewGuid().ToString();
    public MainWindow()
    {
        Text = "MAX Native Client"; Size = new Size(1100, 780); MinimumSize = new Size(820, 550);
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 246, 250);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(6), WrapContents = false };
        toolbar.Controls.AddRange([connect, cancel, refresh, older, logout]);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1080, 680), SplitterDistance = 310, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 240, Panel2MinSize = 380 };
        split.Panel1.Padding = new Padding(6); split.Panel1.Controls.Add(chats); split.Panel1.Controls.Add(filter);
        var composer = new Panel { Dock = DockStyle.Bottom, Height = 86, Padding = new Padding(6) };
        composer.Controls.Add(input); composer.Controls.Add(send);
        qrPanel.Controls.Add(qr); qrPanel.Controls.Add(qrHint);
        split.Panel2.Controls.Add(history); split.Panel2.Controls.Add(qrPanel); split.Panel2.Controls.Add(composer); split.Panel2.Controls.Add(heading);
        Controls.Add(split); Controls.Add(toolbar); Controls.Add(status);
        chats.DrawItem += DrawChat;
        connect.Click += async (_, _) => await StartAsync();
        cancel.Click += (_, _) => operation.Cancel();
        refresh.Click += async (_, _) => await GuardAsync(RefreshAsync);
        older.Click += async (_, _) => await GuardAsync(() => LoadHistoryAsync(true));
        send.Click += async (_, _) => await GuardAsync(SendAsync);
        filter.TextChanged += (_, _) => RenderChats();
        chats.SelectedIndexChanged += async (_, _) => {
            if (renderingChats || chats.SelectedItem is not Chat chat) return;
            if (selectedChat.HasValue) drafts[selectedChat.Value] = input.Text;
            selectedChat = chat.Id; messages.Clear(); history.Clear(); heading.Text = chat.Title;
            input.Text = drafts.GetValueOrDefault(chat.Id, ""); UpdateButtons();
            await GuardAsync(() => LoadHistoryAsync(false));
        };
        input.KeyDown += async (_, e) => {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await GuardAsync(SendAsync); }
        };
        logout.Click += async (_, _) => await GuardAsync(async () => {
            if (connecting || sending) return;
            if (MessageBox.Show(this, "Удалить локальную сессию и черновики?", "MAX", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await StopAsync(); SessionStore.Delete(); deviceId = Guid.NewGuid().ToString();
            allChats.Clear(); names.Clear(); messages.Clear(); drafts.Clear(); selectedChat = null;
            RenderChats(); history.Clear(); input.Clear(); heading.Text = "Выберите чат";
            status.Text = "Сессия удалена. Нажмите «Подключиться / QR» для нового входа.";
        });
        updates.Tick += async (_, _) => { if (authorized && !refreshing) await GuardAsync(RefreshAsync); };
        Shown += async (_, _) => await StartAsync();
        FormClosing += async (_, e) => {
            if (closing) return;
            e.Cancel = true; closing = true; updates.Stop(); await StopAsync(); Close();
        };
    }
    private void DrawChat(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= chats.Items.Count || chats.Items[e.Index] is not Chat chat) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(231, 225, 255) : Color.White);
        e.Graphics.FillRectangle(background, e.Bounds);
        using var bold = new Font(Font, FontStyle.Bold);
        var bounds = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 8, e.Bounds.Width - 20, 24);
        TextRenderer.DrawText(e.Graphics, chat.Title + (chat.Unread > 0 ? $"  ({chat.Unread})" : ""), bold, bounds, Color.FromArgb(43, 34, 65), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        bounds.Y += 26;
        TextRenderer.DrawText(e.Graphics, chat.Preview, Font, bounds, Color.DimGray, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
    }
    private void Post(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!closing && !IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }
    private void UpdateButtons()
    {
        send.Enabled = authorized && selectedChat.HasValue && !sending;
        older.Enabled = authorized && selectedChat.HasValue;
        refresh.Enabled = authorized && !refreshing;
        connect.Enabled = !connecting && !sending;
        cancel.Enabled = connecting;
        logout.Enabled = !connecting && !sending;
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!closing) status.Text = "Операция отменена."; }
        catch (Exception ex) { if (!closing) status.Text = "Ошибка: " + ex.Message; }
    }
    private async Task StopAsync()
    {
        authorized = false; updates.Stop(); operation.Cancel(); qrToken?.TrySetCanceled(); UpdateButtons();
        var old = connection; connection = null;
        if (old != null) await old.DisposeAsync();
        qrPanel.Visible = false; qr.Image?.Dispose(); qr.Image = null;
    }
    private async Task StartAsync()
    {
        if (connecting || sending || closing) return;
        connecting = true; UpdateButtons();
        try
        {
            await StopAsync();
            operation = new CancellationTokenSource();
            var token = operation.Token;
            Credentials? saved;
            try { saved = SessionStore.Load(); }
            catch { throw new IOException("Сессию не удалось прочитать. Удалите её кнопкой «Удалить сессию» и войдите заново."); }
            if (saved != null) deviceId = saved.DeviceId;
            var active = new MaxConnection(); connection = active;
            active.Push += (opcode, payload) => {
                if (opcode == 18) {
                    var accessToken = Data.LoginToken(payload);
                    if (accessToken.Length > 0) qrToken?.TrySetResult(accessToken);
                }
                if (opcode >= 128) Post(() => {
                    if (connection == active && authorized && !refreshing)
                        _ = GuardAsync(RefreshAsync);
                });
            };
            active.Disconnected += () => Post(() => {
                if (connection != active) return;
                authorized = false; updates.Stop(); UpdateButtons();
                status.Text = "Соединение потеряно. Нажмите «Подключиться / QR».";
            });
            status.Text = "Подключение к MAX…";
            await active.ConnectAsync(deviceId, token);
            var access = saved?.Token ?? await AuthorizeQrAsync(active, token);
            status.Text = "Вход в MAX…";
            JsonNode login;
            try { login = await LoginAsync(active, access, token); }
            catch (MaxException ex) when (saved != null && IsExpiredToken(ex.Code)) {
                SessionStore.Delete();
                access = await AuthorizeQrAsync(active, token);
                login = await LoginAsync(active, access, token);
            }
            if (connection != active || token.IsCancellationRequested || !active.Connected) return;
            ownId = Data.Text(login["profile"]?["contact"], "id");
            if (ownId.Length == 0) throw new InvalidDataException("MAX не вернул профиль. Вход не подтверждён.");
            SessionStore.Save(new Credentials(deviceId, access));
            authorized = true; qrPanel.Visible = false; qr.Image?.Dispose(); qr.Image = null;
            qrToken = null; names.Clear();
            foreach (var user in Data.Items(login, "users")) names[Data.Text(user, "id")] = Data.Name(user);
            names[ownId] = "Вы";
            SetChats(login);
            updates.Start(); UpdateButtons();
            status.Text = "Подключён: " + Data.Name(login["profile"]?["contact"]);
            await RefreshAsync();
        }
        catch (OperationCanceledException) {
            await StopAsync(); if (!closing) status.Text = "Подключение отменено.";
        }
        catch (Exception ex) {
            await StopAsync(); if (!closing) status.Text = "Ошибка: " + ex.Message;
        }
        finally { connecting = false; if (!closing) UpdateButtons(); }
    }
    private static bool IsExpiredToken(string code) => code is "auth.token" or "auth.invalid.token" or "auth.expired" or "token.expired";
    private static Task<JsonNode> LoginAsync(MaxConnection active, string access, CancellationToken token) =>
        active.RequestAsync(19, new { token = access, interactive = true, chatsCount = 100, chatsSync = 0, contactsSync = 0, presenceSync = 0, draftsSync = 0 }, token);
    private async Task<string> AuthorizeQrAsync(MaxConnection active, CancellationToken token)
    {
        qrToken = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = await active.RequestAsync(288, new { }, token);
        var link = Data.Text(data, "qrLink"); var track = Data.Text(data, "trackId");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host is "max.ru" or "qr.max.ru") || !string.IsNullOrWhiteSpace(uri.UserInfo) || string.IsNullOrWhiteSpace(track))
            throw new InvalidDataException("Неизвестный формат QR-входа MAX.");
        using var generator = new QRCodeGenerator();
        using var code = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.Q);
        using var renderer = new PngByteQRCode(code);
        using var stream = new MemoryStream(renderer.GetGraphic(8));
        using var image = Image.FromStream(stream);
        qr.Image?.Dispose(); qr.Image = new Bitmap(image); qrPanel.Visible = true; qrPanel.BringToFront();
        qrHint.Text = "Отсканируйте QR приложением MAX на телефоне\nи подтвердите вход. Кнопка «Отмена» прерывает ожидание.";
        status.Text = "Ожидание подтверждения на телефоне…";
        var ttl = Math.Clamp(Data.Number(data, "ttl"), 1000, 300000);
        var interval = Math.Clamp(Data.Number(data, "pollingInterval"), 1000, 30000);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(ttl));
        try {
            while (!qrToken.Task.IsCompleted) {
                var delay = Task.Delay(TimeSpan.FromMilliseconds(interval), deadline.Token);
                await Task.WhenAny(qrToken.Task, delay);
                if (qrToken.Task.IsCompleted) break;
                await delay;
                var poll = active.RequestAsync(289, new { trackId = track }, deadline.Token);
                await Task.WhenAny(qrToken.Task, poll);
                if (qrToken.Task.IsCompleted) {
                    _ = poll.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }
                var response = await poll;
                var access = Data.LoginToken(response);
                if (access.Length > 0) qrToken.TrySetResult(access);
                else if (Data.Text(response["status"], "loginAvailable").Equals("true", StringComparison.OrdinalIgnoreCase)) {
                    var confirmation = await active.RequestAsync(291, new { trackId = track }, deadline.Token);
                    access = Data.LoginToken(confirmation);
                    if (access.Length == 0 && confirmation["passwordChallenge"] is JsonObject challenge)
                        access = await CompletePasswordAsync(active, challenge, deadline.Token);
                    if (access.Length == 0) throw new InvalidDataException("MAX не выдал токен после подтверждения QR.");
                    qrToken.TrySetResult(access);
                }
            }
            return await qrToken.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            throw new TimeoutException("QR-код истёк. Нажмите «Подключиться / QR» для нового кода.");
        }
        catch (MaxException ex) when (ex.Code == "track.not.found") {
            if (qrToken.Task.IsCompletedSuccessfully) return qrToken.Task.Result;
            throw new InvalidOperationException("QR-код недействителен. Нажмите «Подключиться / QR» для нового кода.");
        }
        finally { qrPanel.Visible = false; }
    }
    private async Task<string> CompletePasswordAsync(MaxConnection active, JsonObject challenge, CancellationToken token)
    {
        var track = Data.Text(challenge, "trackId");
        if (track.Length == 0) throw new InvalidDataException("MAX не вернул идентификатор двухэтапной защиты.");
        qrPanel.Visible = false;
        while (true) {
            token.ThrowIfCancellationRequested();
            using var prompt = new Form {
                Text = "Пароль двухэтапной защиты MAX", ClientSize = new Size(420, 160),
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
            };
            var hint = new Label { Dock = DockStyle.Top, Height = 56, Padding = new Padding(10), Text = "Введите пароль MAX. " + Data.Text(challenge, "hint"), AutoEllipsis = true };
            var field = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, MaxLength = 1024 };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "Войти", DialogResult = DialogResult.OK };
            var cancelPassword = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
            buttons.Controls.AddRange([ok, cancelPassword]);
            prompt.Controls.Add(field); prompt.Controls.Add(hint); prompt.Controls.Add(buttons);
            prompt.AcceptButton = ok; prompt.CancelButton = cancelPassword;
            if (prompt.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            if (field.Text.Length == 0) continue;
            try {
                var password = field.Text; field.Clear();
                var result = await active.RequestAsync(115, new { trackId = track, password }, token);
                var access = Data.LoginToken(result);
                if (access.Length > 0) return access;
                throw new InvalidDataException("MAX не выдал токен после проверки пароля.");
            }
            catch (MaxException ex) when (ex.Code.Contains("password", StringComparison.OrdinalIgnoreCase)) {
                status.Text = "MAX отклонил пароль. Попробуйте снова или отмените вход.";
            }
        }
    }
    private void SetChats(JsonNode data)
    {
        allChats.Clear();
        foreach (var node in Data.Items(data, "chats")) {
            var id = Data.Number(node, "id"); var title = Data.Text(node, "title");
            if (title.Length == 0 && node["participants"] is JsonObject participants) {
                var other = participants.FirstOrDefault(pair => pair.Key != ownId).Key;
                if (other != null) title = names.GetValueOrDefault(other, other);
            }
            allChats.Add(new Chat(id, title.Length > 0 ? title : $"Чат {id}",
                Data.Text(node["lastMessage"], "text").Replace("\n", " "), Data.Number(node, "newMessages"), Data.Number(node["lastMessage"], "time")));
        }
        RenderChats();
    }
    private void RenderChats()
    {
        renderingChats = true; chats.BeginUpdate();
        try {
            chats.Items.Clear();
            foreach (var chat in allChats.Where(c => c.Title.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.LastTime)) {
                chats.Items.Add(chat); if (chat.Id == selectedChat) chats.SelectedItem = chat;
            }
        } finally { chats.EndUpdate(); renderingChats = false; }
    }
    private async Task RefreshAsync()
    {
        if (!authorized || connection == null || refreshing) return;
        refreshing = true; UpdateButtons();
        try {
            var active = connection; var token = operation.Token;
            var data = await active.RequestAsync(53, new { count = 100, marker = 0 }, token);
            if (active != connection) return;
            if (data["chats"] is not JsonArray) throw new InvalidDataException("MAX не вернул список чатов.");
            SetChats(data);
            if (selectedChat.HasValue) await LoadHistoryAsync(false);
            status.Text = "Синхронизировано " + DateTime.Now.ToString("HH:mm:ss");
        } finally { refreshing = false; if (!closing) UpdateButtons(); }
    }
    private async Task LoadHistoryAsync(bool previous)
    {
        if (!authorized || connection == null || !selectedChat.HasValue) return;
        var active = connection; var id = selectedChat.Value; var token = operation.Token;
        await historyGate.WaitAsync(token);
        try {
            if (id != selectedChat || active != connection || !authorized) return;
            var from = previous && messages.Count > 0 ? messages.Values.Min(m => m.Time) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var data = await active.RequestAsync(49, new { chatId = id, forward = 0, backward = 50, backwardTime = 0, forwardTime = 0, getChat = false, from, getMessages = true, interactive = false }, token);
            if (id != selectedChat || active != connection) return;
            if (data["messages"] is not JsonArray) throw new InvalidDataException("MAX не вернул историю сообщений.");
            var page = Data.Items(data, "messages").ToList();
            var ids = page.Select(m => Data.Text(m, "sender")).Where(s => long.TryParse(s, out _) && !names.ContainsKey(s)).Distinct().Select(long.Parse).Take(100).ToArray();
            if (ids.Length > 0) {
                try {
                    var contacts = await active.RequestAsync(32, new { contactIds = ids }, token);
                    if (id != selectedChat || active != connection) return;
                    foreach (var user in Data.Items(contacts, "contacts")) names[Data.Text(user, "id")] = Data.Name(user);
                } catch (MaxException) { /* History remains readable with sender IDs. */ }
            }
            if (!previous && page.Count > 0) {
                var earliest = page.Min(m => Data.Number(m, "time"));
                foreach (var key in messages.Where(pair => pair.Value.Time >= earliest).Select(pair => pair.Key).ToArray()) messages.Remove(key);
            } else if (!previous && page.Count == 0) messages.Clear();
            foreach (var m in page) {
                var messageId = Data.Text(m, "id"); if (messageId.Length == 0) continue;
                var text = Data.Text(m, "text");
                var attachments = Data.Items(m, "attaches").Select(a => Data.Text(a, "_type")).ToArray();
                if (attachments.Length > 0) text += "\n[Вложения: " + string.Join(", ", attachments) + "]";
                messages[messageId] = new Message(messageId, Data.Number(m, "time"), Data.Text(m, "sender"), text);
            }
            RenderHistory(previous);
            if (previous && page.Count == 0) status.Text = "Более старые сообщения не найдены.";
        } finally { historyGate.Release(); }
    }
    private void RenderHistory(bool previous)
    {
        var selection = history.SelectionStart;
        var end = !previous && (history.TextLength == 0 || selection >= history.TextLength - 2);
        history.Clear();
        foreach (var m in messages.Values.OrderBy(m => m.Time).ThenBy(m => m.Id)) {
            string stamp;
            try { stamp = DateTimeOffset.FromUnixTimeMilliseconds(m.Time).LocalDateTime.ToString("dd.MM HH:mm"); }
            catch (ArgumentOutOfRangeException) { stamp = ""; }
            history.SelectionColor = m.SenderId == ownId ? Color.FromArgb(104, 62, 216) : Color.FromArgb(32, 95, 136);
            history.AppendText($"{names.GetValueOrDefault(m.SenderId, m.SenderId.Length == 0 ? "Канал" : m.SenderId)} · {stamp}\n");
            history.SelectionColor = Color.FromArgb(40, 40, 45);
            history.AppendText(m.Text + "\n\n");
        }
        history.SelectionStart = end ? history.TextLength : Math.Min(selection, history.TextLength);
        history.ScrollToCaret();
    }
    private async Task SendAsync()
    {
        if (!authorized || connection == null || !selectedChat.HasValue || string.IsNullOrWhiteSpace(input.Text) || sending) return;
        var active = connection; var id = selectedChat.Value; var text = input.Text; var token = operation.Token;
        sending = true; UpdateButtons();
        try {
            JsonNode result;
            try {
                result = await active.RequestAsync(64, new { chatId = id,
                    message = new { text, cid = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), elements = Array.Empty<object>(), attaches = Array.Empty<object>() }, notify = true }, token);
            }
            catch (TimeoutException) { throw new IOException("Нет подтверждения отправки. Проверьте историю перед повтором."); }
            if (Data.Text(result["message"], "id").Length == 0)
                throw new InvalidDataException("MAX не подтвердил отправку. Проверьте историю перед повтором.");
            if (active != connection) return;
            if (id == selectedChat && input.Text == text) input.Clear();
            if (drafts.GetValueOrDefault(id) == text) drafts.Remove(id);
            if (id == selectedChat) await LoadHistoryAsync(false);
            status.Text = "Сообщение отправлено.";
        } finally { sending = false; if (!closing) UpdateButtons(); }
    }
}
