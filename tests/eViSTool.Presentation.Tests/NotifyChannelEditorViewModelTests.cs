using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Окно канала оповещений: поля под тип, имя по умолчанию, правило «пустое поле секрета — оставить сохранённый», чат
/// Telegram (найденный или вписанный руками), поиск чатов, проверочное сообщение и сборка канала с проверками. Поиск и отправка подменены делегатами: в сеть тесты не ходят. Токены и ссылки выдуманные.
/// </summary>
public sealed class NotifyChannelEditorViewModelTests
{
    private const string Token = "123456789:made-up-token-for-tests-only";
    private const string OtherToken = "987654321:another-made-up-token-xyz";
    private const string DiscordHook = "https://discord.com/api/webhooks/123456/made-up-token";
    private const string Hook = "https://hooks.example.org/evistool?key=made-up";

    private static NotifyChannelEditorViewModel Model(NotifyChannel channel, bool isNew,
        Func<string, Task<IReadOnlyList<TelegramChat>>>? find = null, Func<NotifyChannel, NotifyMessage, Task>? send = null) =>
        new(channel, isNew,
            find ?? (_ => throw new Xunit.Sdk.XunitException("поиск чатов в этом тесте не ожидался")),
            send ?? ((_, _) => throw new Xunit.Sdk.XunitException("отправка в этом тесте не ожидалась")));

    /// <summary>Новый канал — как его открывает «Добавить канал» в настройках.</summary>
    private static NotifyChannelEditorViewModel New(Func<string, Task<IReadOnlyList<TelegramChat>>>? find = null,
        Func<NotifyChannel, NotifyMessage, Task>? send = null) =>
        Model(new NotifyChannel { Kind = NotifyKind.Telegram, Name = nameof(NotifyKind.Telegram) }, isNew: true, find, send);

    /// <summary>Сохранённый канал с зашифрованным секретом.</summary>
    private static NotifyChannel Saved(NotifyKind kind, string secret, string name = "Сохранённый") =>
        new() { Id = "saved-1", Kind = kind, Name = name, SecretProtected = NotifySecret.Protect(secret) };

    private static void Pick(NotifyChannelEditorViewModel vm, NotifyKind kind) => vm.SelectedKind = vm.Kinds.Single(k => k.Kind == kind);

    /// <summary>«Сохранить» и ответ окна; не сохранилось — null (и событие не пришло).</summary>
    private static NotifyChannel? Save(NotifyChannelEditorViewModel vm)
    {
        NotifyChannel? finished = null;
        void OnFinished(NotifyChannel c) => finished = c;
        vm.Finished += OnFinished;
        vm.SaveCommand.Execute(null);
        vm.Finished -= OnFinished;
        Assert.Same(finished, vm.Result);
        return finished;
    }

    private static void AssertError(NotifyChannelEditorViewModel vm, string key)
    {
        Assert.Equal(Loc.T(key), vm.StatusText);
        Assert.True(vm.StatusIsError);
    }

    private static Func<string, Task<IReadOnlyList<TelegramChat>>> Found(params TelegramChat[] chats) =>
        _ => Task.FromResult<IReadOnlyList<TelegramChat>>(chats);

    // ---------- открытие

    [Fact]
    public void New_Opens_AsTelegram_WithEmptyFieldsAndPlainHints()
    {
        var vm = New();

        Assert.Equal(["Telegram", "Discord", "ntfy", Loc.T("notify.whKind")], vm.Kinds.Select(k => k.Title));
        Assert.Equal([NotifyKind.Telegram, NotifyKind.Discord, NotifyKind.Ntfy, NotifyKind.Webhook], vm.Kinds.Select(k => k.Kind));
        Assert.True(vm.IsKindEditable);
        Assert.Equal(NotifyKind.Telegram, vm.Kind);
        Assert.True(vm.IsTelegram);
        Assert.False(vm.IsDiscord || vm.IsNtfy || vm.IsWebhook);
        Assert.Equal(Loc.T("notify.tgIntro"), vm.IntroText);
        Assert.Equal("Telegram", vm.Name);
        Assert.Equal(Loc.T("notify.tgTokenHint"), vm.TokenHint);
        Assert.Equal(Loc.T("notify.dcUrlHint"), vm.DiscordHint);
        Assert.Equal(Loc.T("notify.whUrlHint"), vm.WebhookHint);
        Assert.Equal("", vm.TelegramToken);
        Assert.Empty(vm.Chats);
        Assert.Null(vm.SelectedChat);
        Assert.Equal("", vm.ChatText);
        Assert.Equal("", vm.TopicText);
        Assert.Matches("^evistool-[0-9a-f]{16}$", vm.NtfyTopic); // тема ntfy готова сразу — длинная и случайная
        Assert.Equal("", vm.NtfyServer);
        Assert.Equal(WebhookTemplates.Json, vm.Template);
        Assert.Null(vm.SelectedPreset);
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.StatusIsError);
        Assert.Null(vm.Result);
    }

    [Theory]
    [InlineData(NotifyKind.Telegram, "notify.tgIntro")]
    [InlineData(NotifyKind.Discord, "notify.dcIntro")]
    [InlineData(NotifyKind.Ntfy, "notify.ntfyIntro")]
    [InlineData(NotifyKind.Webhook, "notify.whIntro")]
    public void Saved_Opens_WithItsKindLocked_AndKeptSecretHints(NotifyKind kind, string intro)
    {
        var vm = Model(Saved(kind, "made-up-secret", name: "Telegram"), isNew: false);

        Assert.False(vm.IsKindEditable);
        Assert.Equal(kind, vm.Kind);
        Assert.Equal(kind == NotifyKind.Telegram, vm.IsTelegram);
        Assert.Equal(kind == NotifyKind.Discord, vm.IsDiscord);
        Assert.Equal(kind == NotifyKind.Ntfy, vm.IsNtfy);
        Assert.Equal(kind == NotifyKind.Webhook, vm.IsWebhook);
        Assert.Equal(Loc.T(intro), vm.IntroText);
        Assert.Equal("Telegram", vm.Name); // у сохранённого имя не подстраивается под тип, даже если совпадает с названием типа
        // секрет сохранён — «оставь поле пустым», а сами поля пустые
        Assert.Equal(Loc.T("notify.tgTokenKept"), vm.TokenHint);
        Assert.Equal(Loc.T("notify.secretKept"), vm.DiscordHint);
        Assert.Equal(Loc.T("notify.secretKept"), vm.WebhookHint);
        Assert.Equal("", vm.TelegramToken);
        Assert.Equal("", vm.DiscordUrl);
        Assert.Equal("", vm.WebhookUrl);
    }

    [Fact]
    public void SavedTelegram_ShowsItsChat_AndTopic()
    {
        var channel = Saved(NotifyKind.Telegram, Token) with { ChatId = "-1001234", ChatTitle = "Наш сервер › Анонсы", TopicId = 42 };
        var vm = Model(channel, isNew: false);

        var chat = Assert.Single(vm.Chats);
        Assert.Equal(new TelegramChat("-1001234", "Наш сервер › Анонсы", 42), chat);
        Assert.Same(chat, vm.SelectedChat);
        Assert.Equal("Наш сервер › Анонсы", vm.ChatText);
        Assert.Equal("42", vm.TopicText);
    }

    [Fact]
    public void SavedTelegram_WithoutChatTitle_ShowsChatId()
    {
        var vm = Model(Saved(NotifyKind.Telegram, Token) with { ChatId = "555" }, isNew: false);
        Assert.Equal("555", vm.ChatText);
        Assert.Equal("555", vm.SelectedChat?.Title);
        Assert.Equal("", vm.TopicText);
    }

    [Fact]
    public void SavedTelegram_WithoutChat_HasEmptyList()
    {
        var vm = Model(Saved(NotifyKind.Telegram, Token) with { TopicId = 9 }, isNew: false);
        Assert.Empty(vm.Chats);
        Assert.Equal("", vm.ChatText);
        Assert.Equal("9", vm.TopicText); // тема — как была, даже без чата
    }

    [Fact]
    public void SavedNtfy_ShowsTopicAndServer()
    {
        var vm = Model(Saved(NotifyKind.Ntfy, "my-topic_1") with { Url = "https://ntfy.example.org" }, isNew: false);
        Assert.Equal("my-topic_1", vm.NtfyTopic);
        Assert.Equal("https://ntfy.example.org", vm.NtfyServer);
    }

    [Fact]
    public void SavedNtfy_WithUnreadableTopic_GetsNewOne()
    {
        var vm = Model(new NotifyChannel { Kind = NotifyKind.Ntfy, Name = "ntfy", SecretProtected = "not-a-protected-string" }, isNew: false);
        Assert.Matches("^evistool-[0-9a-f]{16}$", vm.NtfyTopic);
        Assert.Equal("", vm.NtfyServer);
    }

    [Fact]
    public void OtherKinds_IgnoreNtfyAndWebhookFields()
    {
        // поля другого типа в канале (так не бывает, но окно их не берёт) — не подставляются
        var vm = Model(Saved(NotifyKind.Discord, DiscordHook) with { Url = "https://ntfy.example.org", Template = "{}" }, isNew: false);
        Assert.Equal("", vm.NtfyServer);
        Assert.Matches("^evistool-[0-9a-f]{16}$", vm.NtfyTopic);
        Assert.Equal(WebhookTemplates.Json, vm.Template);
    }

    [Theory]
    [InlineData(WebhookTemplates.Slack, WebhookTemplates.Slack)]
    [InlineData("", WebhookTemplates.Json)]
    [InlineData(null, WebhookTemplates.Json)]
    public void SavedWebhook_ShowsItsTemplate_OrPlainJson(string? template, string expected)
    {
        var vm = Model(Saved(NotifyKind.Webhook, Hook) with { Template = template }, isNew: false);
        Assert.Equal(expected, vm.Template);
    }

    // ---------- тип и имя

    [Theory]
    [InlineData(NotifyKind.Discord, "notify.dcIntro")]
    [InlineData(NotifyKind.Ntfy, "notify.ntfyIntro")]
    [InlineData(NotifyKind.Webhook, "notify.whIntro")]
    [InlineData(NotifyKind.Telegram, "notify.tgIntro")]
    public void SwitchingKind_ShowsItsFieldsAndIntro_AndClearsStatus(NotifyKind kind, string intro)
    {
        var vm = New();
        Pick(vm, kind == NotifyKind.Ntfy ? NotifyKind.Discord : NotifyKind.Ntfy); // сначала другой тип, с ошибкой
        vm.NtfyTopic = "плохая тема!";
        Assert.Null(Save(vm));
        Assert.True(vm.StatusIsError);

        Pick(vm, kind);
        Assert.Equal(kind, vm.Kind);
        Assert.Equal(kind == NotifyKind.Telegram, vm.IsTelegram);
        Assert.Equal(kind == NotifyKind.Discord, vm.IsDiscord);
        Assert.Equal(kind == NotifyKind.Ntfy, vm.IsNtfy);
        Assert.Equal(kind == NotifyKind.Webhook, vm.IsWebhook);
        Assert.Equal(Loc.T(intro), vm.IntroText);
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public void SwitchingKind_RaisesFlagsForTheView()
    {
        var vm = New();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Pick(vm, NotifyKind.Discord);
        Assert.Contains(nameof(vm.IsTelegram), changed);
        Assert.Contains(nameof(vm.IsDiscord), changed);
        Assert.Contains(nameof(vm.IsNtfy), changed);
        Assert.Contains(nameof(vm.IsWebhook), changed);
        Assert.Contains(nameof(vm.IntroText), changed);
        Assert.Contains(nameof(vm.Kind), changed);
    }

    [Fact]
    public void NewChannel_NameFollowsKind_UntilEdited()
    {
        var vm = New();
        Pick(vm, NotifyKind.Discord);
        Assert.Equal("Discord", vm.Name);
        Pick(vm, NotifyKind.Webhook);
        Assert.Equal(Loc.T("notify.whKind"), vm.Name);

        vm.Name = ""; // стёрли — снова по типу
        Pick(vm, NotifyKind.Ntfy);
        Assert.Equal("ntfy", vm.Name);

        vm.Name = "Мой телефон"; // своё — больше не трогаем
        Pick(vm, NotifyKind.Telegram);
        Assert.Equal("Мой телефон", vm.Name);

        vm.Name = "  "; // одни пробелы — тоже своё
        Pick(vm, NotifyKind.Discord);
        Assert.Equal("  ", vm.Name);
    }

    [Fact]
    public void SavedChannel_NameDoesNotFollowKind()
    {
        var vm = Model(Saved(NotifyKind.Telegram, Token, name: "Telegram"), isNew: false);
        Pick(vm, NotifyKind.Discord); // окно не даёт, но модель всё равно не трогает имя
        Assert.Equal("Telegram", vm.Name);
    }

    // ---------- секреты и состояние

    [Theory]
    [InlineData(nameof(NotifyChannelEditorViewModel.TelegramToken))]
    [InlineData(nameof(NotifyChannelEditorViewModel.DiscordUrl))]
    [InlineData(nameof(NotifyChannelEditorViewModel.WebhookUrl))]
    public void TypingSecret_ClearsStatus(string field)
    {
        var vm = New();
        Assert.Null(Save(vm));
        AssertError(vm, "notify.tgNeedToken");

        typeof(NotifyChannelEditorViewModel).GetProperty(field)!.SetValue(vm, "x");
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public void TypingOtherFields_KeepsStatus()
    {
        var vm = New();
        Assert.Null(Save(vm));
        vm.Name = "Другое";
        vm.ChatText = "123";
        vm.TopicText = "5";
        AssertError(vm, "notify.tgNeedToken");
    }

    [Fact]
    public void NewTopic_MakesAnotherRandomTopic()
    {
        var vm = New();
        var before = vm.NtfyTopic;
        vm.NewTopicCommand.Execute(null);
        Assert.Matches("^evistool-[0-9a-f]{16}$", vm.NtfyTopic);
        Assert.NotEqual(before, vm.NtfyTopic);
    }

    [Fact]
    public void Preset_ReplacesTemplate()
    {
        var vm = New();
        Assert.Equal([Loc.T("notify.whPresetJson"), "Slack", "Home Assistant"], vm.Presets.Select(p => p.Title));
        Assert.Equal([WebhookTemplates.Json, WebhookTemplates.Slack, WebhookTemplates.HomeAssistant], vm.Presets.Select(p => p.Template));

        vm.Template = "{ \"своё\": 1 }";
        vm.SelectedPreset = vm.Presets[1];
        Assert.Equal(WebhookTemplates.Slack, vm.Template);
        vm.Template = "{ \"правка\": 2 }";
        vm.SelectedPreset = null; // выбор сбросили — шаблон не трогаем
        Assert.Equal("{ \"правка\": 2 }", vm.Template);
        vm.SelectedPreset = vm.Presets[2];
        Assert.Equal(WebhookTemplates.HomeAssistant, vm.Template);
    }

    // ---------- Telegram: сохранение

    [Fact]
    public void Telegram_NeedsToken()
    {
        var vm = New();
        vm.ChatText = "123";
        vm.TelegramToken = "   ";
        Assert.Null(Save(vm));
        AssertError(vm, "notify.tgNeedToken");
    }

    [Fact]
    public void Telegram_NeedsChat()
    {
        var vm = New();
        vm.TelegramToken = Token;
        vm.ChatText = "  ";
        Assert.Null(Save(vm));
        AssertError(vm, "notify.tgNoChat");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void Telegram_RejectsBadTopic(string topic)
    {
        var vm = New();
        vm.TelegramToken = Token;
        vm.ChatText = "123";
        vm.TopicText = topic;
        Assert.Null(Save(vm));
        AssertError(vm, "notify.tgBadTopic");
    }

    [Fact]
    public void Telegram_TypedChatAndToken_MakeTheChannel()
    {
        var vm = New();
        vm.Name = "  Мой бот  ";
        vm.TelegramToken = "  " + Token + "  ";
        vm.ChatText = "  -100777  ";
        vm.TopicText = " 42 ";

        var channel = Save(vm);

        Assert.NotNull(channel);
        Assert.Equal(NotifyKind.Telegram, channel.Kind);
        Assert.Equal("Мой бот", channel.Name);
        Assert.Equal(Token, channel.Secret);
        Assert.Equal("-100777", channel.ChatId);
        Assert.Equal("-100777", channel.ChatTitle); // вписанный руками ID — он же подпись
        Assert.Equal(42, channel.TopicId);
        Assert.Null(channel.Url);
        Assert.Null(channel.Template);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void Telegram_EmptyTopic_IsGeneralChat_AndEmptyName_IsKindTitle()
    {
        var vm = New();
        vm.Name = "   ";
        vm.TelegramToken = Token;
        vm.ChatText = "123";
        vm.TopicText = "  ";
        var channel = Save(vm);
        Assert.NotNull(channel);
        Assert.Null(channel.TopicId);
        Assert.Equal("Telegram", channel.Name);
    }

    [Fact]
    public void Telegram_SelectedChat_GivesItsIdAndTitle()
    {
        var vm = New(Found(new TelegramChat("111", "@erney")));
        vm.TelegramToken = Token;
        vm.FindChatsCommand.Execute(null);

        var channel = Save(vm);
        Assert.Equal("111", channel?.ChatId);
        Assert.Equal("@erney", channel?.ChatTitle);
    }

    [Fact]
    public void Telegram_EditedTextOfSelectedChat_IsTypedId()
    {
        var vm = New(Found(new TelegramChat("111", "@erney")));
        vm.TelegramToken = Token;
        vm.FindChatsCommand.Execute(null);
        vm.ChatText = "@erney2"; // правили текст выбранного — это уже свой ID

        var channel = Save(vm);
        Assert.Equal("@erney2", channel?.ChatId);
        Assert.Equal("@erney2", channel?.ChatTitle);
    }

    [Fact]
    public void Telegram_FullTitleOfListedChat_IsThatChat()
    {
        // поле со списком само выбирает пункт по тексту; модель не зависит от того, успело ли
        var vm = New(Found(new TelegramChat("111", "@erney"), new TelegramChat("-1002", "Группа")));
        vm.TelegramToken = Token;
        vm.FindChatsCommand.Execute(null);
        vm.SelectedChat = null;
        vm.ChatText = "Группа";

        var channel = Save(vm);
        Assert.Equal("-1002", channel?.ChatId);
        Assert.Equal("Группа", channel?.ChatTitle);
    }

    [Fact]
    public void Telegram_SavedToken_IsKept_WhenFieldEmpty()
    {
        var original = Saved(NotifyKind.Telegram, Token) with { ChatId = "-1001", ChatTitle = "Наш сервер" };
        var vm = Model(original, isNew: false);

        var channel = Save(vm);

        Assert.NotNull(channel);
        Assert.Equal(original.SecretProtected, channel.SecretProtected); // не перешифрован
        Assert.Equal(original.Id, channel.Id);
        Assert.Equal("-1001", channel.ChatId);
        Assert.Equal("Наш сервер", channel.ChatTitle);
    }

    [Fact]
    public void Telegram_NewToken_ReplacesSaved()
    {
        var original = Saved(NotifyKind.Telegram, Token) with { ChatId = "-1001" };
        var vm = Model(original, isNew: false);
        vm.TelegramToken = OtherToken;

        var channel = Save(vm);

        Assert.NotNull(channel);
        Assert.NotEqual(original.SecretProtected, channel.SecretProtected);
        Assert.Equal(OtherToken, channel.Secret);
    }

    [Fact]
    public void Telegram_UnreadableSavedToken_NeedsNewOne()
    {
        // зашифровано на другом компьютере — расшифровать нельзя, поле пустое
        var vm = Model(new NotifyChannel { Kind = NotifyKind.Telegram, Name = "tg", SecretProtected = "not-a-protected-string", ChatId = "1" }, isNew: false);
        Assert.Null(Save(vm));
        AssertError(vm, "notify.tgNeedToken");
    }

    [Fact]
    public void SavedSecret_OfAnotherKind_IsNotUsed()
    {
        var vm = Model(Saved(NotifyKind.Telegram, Token), isNew: false);
        Pick(vm, NotifyKind.Discord); // окно не даёт сменить тип сохранённого; модель всё равно не берёт чужой секрет
        Assert.Null(Save(vm));
        AssertError(vm, "notify.dcNeedUrl");
    }

    // ---------- Discord, ntfy, вебхук: сохранение

    [Fact]
    public void Discord_NeedsUrl_AndAValidOne()
    {
        var vm = New();
        Pick(vm, NotifyKind.Discord);
        Assert.Null(Save(vm));
        AssertError(vm, "notify.dcNeedUrl");

        vm.DiscordUrl = "https://example.org/api/webhooks/1/abc";
        Assert.Null(Save(vm));
        AssertError(vm, "notify.dcBadUrl");
    }

    [Fact]
    public void Discord_MakesTheChannel_AndDropsOtherKindsFields()
    {
        // в сохранённом канале остались поля другого типа — в новом их нет
        var original = Saved(NotifyKind.Discord, DiscordHook) with { ChatId = "1", ChatTitle = "старое", TopicId = 3, Url = "https://x.example", Template = "{}" };
        var vm = Model(original, isNew: false);
        vm.DiscordUrl = " " + DiscordHook.Replace("123456", "654321") + " ";

        var channel = Save(vm);

        Assert.NotNull(channel);
        Assert.Equal(NotifyKind.Discord, channel.Kind);
        Assert.Equal(DiscordHook.Replace("123456", "654321"), channel.Secret);
        Assert.Null(channel.ChatId);
        Assert.Null(channel.ChatTitle);
        Assert.Null(channel.TopicId);
        Assert.Null(channel.Url);
        Assert.Null(channel.Template);
        Assert.Equal(original.Id, channel.Id);
    }

    [Fact]
    public void Discord_SavedUrl_IsKept_WhenFieldEmpty()
    {
        var original = Saved(NotifyKind.Discord, DiscordHook);
        var vm = Model(original, isNew: false);
        var channel = Save(vm);
        Assert.Equal(original.SecretProtected, channel?.SecretProtected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("плохая тема!")]
    [InlineData("тема с пробелом")]
    public void Ntfy_RejectsBadTopic(string topic)
    {
        var vm = New();
        Pick(vm, NotifyKind.Ntfy);
        vm.NtfyTopic = topic;
        Assert.Null(Save(vm));
        AssertError(vm, "notify.ntfyBadTopic");
    }

    [Theory]
    [InlineData("ntfy.example.org")]
    [InlineData("ftp://ntfy.example.org")]
    public void Ntfy_RejectsBadServer(string server)
    {
        var vm = New();
        Pick(vm, NotifyKind.Ntfy);
        vm.NtfyServer = server;
        Assert.Null(Save(vm));
        AssertError(vm, "notify.ntfyBadServer");
    }

    [Theory]
    [InlineData("  https://ntfy.example.org  ", "https://ntfy.example.org")]
    [InlineData("http://192.0.2.5:8080", "http://192.0.2.5:8080")]
    [InlineData("   ", null)]
    public void Ntfy_MakesTheChannel(string server, string? url)
    {
        var vm = New();
        Pick(vm, NotifyKind.Ntfy);
        vm.NtfyTopic = "  evistool-test_topic  ";
        vm.NtfyServer = server;

        var channel = Save(vm);

        Assert.NotNull(channel);
        Assert.Equal(NotifyKind.Ntfy, channel.Kind);
        Assert.Equal("ntfy", channel.Name);
        Assert.Equal("evistool-test_topic", channel.Secret);
        Assert.Equal("evistool-test_topic", channel.ChatTitle);
        Assert.Equal(url, channel.Url);
    }

    [Fact]
    public void Webhook_NeedsUrl_AndHttp()
    {
        var vm = New();
        Pick(vm, NotifyKind.Webhook);
        Assert.Null(Save(vm));
        AssertError(vm, "notify.whNeedUrl");

        foreach (var bad in new[] { "hooks.example.org/evistool", "ftp://hooks.example.org/x" })
        {
            vm.WebhookUrl = bad;
            Assert.Null(Save(vm));
            AssertError(vm, "notify.whBadUrl");
        }
    }

    [Fact]
    public void Webhook_RejectsTemplateThatIsNotJson()
    {
        var vm = New();
        Pick(vm, NotifyKind.Webhook);
        vm.WebhookUrl = Hook;
        vm.Template = "{ \"text\": {text} }"; // без кавычек вокруг подстановки — не JSON
        Assert.Null(Save(vm));
        AssertError(vm, "notify.whBadTemplate");
    }

    [Fact]
    public void Webhook_MakesTheChannel()
    {
        var vm = New();
        Pick(vm, NotifyKind.Webhook);
        vm.WebhookUrl = Hook;
        vm.SelectedPreset = vm.Presets[2];

        var channel = Save(vm);

        Assert.NotNull(channel);
        Assert.Equal(NotifyKind.Webhook, channel.Kind);
        Assert.Equal(Hook, channel.Secret);
        Assert.Equal(WebhookTemplates.HomeAssistant, channel.Template);
        Assert.Equal("hooks.example.org", channel.ChatTitle); // в списке каналов видно, куда уходит, без ключа из ссылки
        Assert.Null(channel.Url);
    }

    [Fact]
    public void Webhook_SavedUrl_IsKept_WhenFieldEmpty()
    {
        var original = Saved(NotifyKind.Webhook, Hook) with { Template = WebhookTemplates.Slack };
        var vm = Model(original, isNew: false);
        var channel = Save(vm);
        Assert.Equal(original.SecretProtected, channel?.SecretProtected);
        Assert.Equal(WebhookTemplates.Slack, channel?.Template);
        Assert.Equal("hooks.example.org", channel?.ChatTitle);
    }

    // ---------- поиск чатов Telegram

    [Fact]
    public async Task Find_WithoutToken_AsksForIt()
    {
        var vm = New(); // поиск не подменён: вызвать его — ошибка теста
        await vm.FindChatsCommand.ExecuteAsync(null);
        AssertError(vm, "notify.tgNeedToken");
    }

    [Fact]
    public async Task Find_UsesTypedToken_OrSavedOne()
    {
        var asked = new List<string>();
        Func<string, Task<IReadOnlyList<TelegramChat>>> find = token =>
        {
            asked.Add(token);
            return Task.FromResult<IReadOnlyList<TelegramChat>>([new TelegramChat("1", "@me")]);
        };

        var saved = Model(Saved(NotifyKind.Telegram, Token), isNew: false, find);
        await saved.FindChatsCommand.ExecuteAsync(null);
        saved.TelegramToken = "  " + OtherToken + " ";
        await saved.FindChatsCommand.ExecuteAsync(null);

        Assert.Equal([Token, OtherToken], asked);
    }

    [Fact]
    public async Task Find_OneChat_SelectsIt()
    {
        var requested = 0;
        var vm = New(Found(new TelegramChat("111", "@erney")));
        vm.ChatListRequested += () => requested++;
        vm.TopicText = "5";
        vm.TelegramToken = Token;

        await vm.FindChatsCommand.ExecuteAsync(null);

        Assert.Single(vm.Chats);
        Assert.Equal("111", vm.SelectedChat?.Id);
        Assert.Equal("@erney", vm.ChatText);
        Assert.Equal("", vm.TopicText); // у чата без темы — общий чат
        Assert.Equal(Loc.T("notify.tgFound", "@erney"), vm.StatusText);
        Assert.False(vm.StatusIsError);
        Assert.Equal(0, requested); // список раскрывать незачем
    }

    [Fact]
    public async Task Find_ManyChats_SelectsFirst_AndAsksToOpenTheList()
    {
        var requested = 0;
        var vm = New(Found(new TelegramChat("-1002", "Группа › Анонсы", 7), new TelegramChat("111", "@erney")));
        vm.ChatListRequested += () => requested++;
        vm.TelegramToken = Token;

        await vm.FindChatsCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Chats.Count);
        Assert.Same(vm.Chats[0], vm.SelectedChat);
        Assert.Equal("Группа › Анонсы", vm.ChatText);
        Assert.Equal("7", vm.TopicText); // тема форума подставилась
        Assert.Equal(Loc.T("notify.tgFoundMany", 2), vm.StatusText);
        Assert.False(vm.StatusIsError);
        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task Find_NoChats_DropsSavedChat()
    {
        var vm = Model(Saved(NotifyKind.Telegram, Token) with { ChatId = "-1001", ChatTitle = "Наш сервер" }, isNew: false, Found());

        await vm.FindChatsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Chats);
        Assert.Null(vm.SelectedChat);
        Assert.Equal("", vm.ChatText); // подпись пропавшего чата не уйдёт в канал как ID
        AssertError(vm, "notify.tgNoChats");
    }

    [Fact]
    public async Task Find_NoChats_KeepsTypedId()
    {
        var vm = New(Found());
        vm.TelegramToken = Token;
        vm.ChatText = "-100777";

        await vm.FindChatsCommand.ExecuteAsync(null);

        Assert.Equal("-100777", vm.ChatText);
        AssertError(vm, "notify.tgNoChats");
        Assert.Equal("-100777", Save(vm)?.ChatId);
    }

    [Fact]
    public async Task Find_Error_IsShown_AndListStays()
    {
        var vm = Model(Saved(NotifyKind.Telegram, Token) with { ChatId = "-1001", ChatTitle = "Наш сервер" }, isNew: false,
            _ => throw new NotifyException("Telegram не принимает этот токен"));

        await vm.FindChatsCommand.ExecuteAsync(null);

        Assert.Equal("Telegram не принимает этот токен", vm.StatusText);
        Assert.True(vm.StatusIsError);
        Assert.Single(vm.Chats);
        Assert.Equal("Наш сервер", vm.ChatText);
    }

    [Fact]
    public async Task Find_IsDisabled_WhileSearching()
    {
        var reply = new TaskCompletionSource<IReadOnlyList<TelegramChat>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = New(_ => reply.Task);
        vm.TelegramToken = Token;

        var running = vm.FindChatsCommand.ExecuteAsync(null);
        Assert.False(vm.FindChatsCommand.CanExecute(null));
        Assert.True(vm.TestCommand.CanExecute(null)); // проверочное — своей кнопкой

        reply.SetResult([new TelegramChat("1", "@me")]);
        await running;
        Assert.True(vm.FindChatsCommand.CanExecute(null));
    }

    [Fact]
    public void SelectingChat_FillsItsTopic()
    {
        var vm = New();
        vm.TopicText = "99";
        vm.SelectedChat = new TelegramChat("-1002", "Группа › Правила", 12);
        Assert.Equal("12", vm.TopicText);
        vm.SelectedChat = new TelegramChat("-1002", "Группа");
        Assert.Equal("", vm.TopicText);
        vm.TopicText = "5";
        vm.SelectedChat = null; // выбор сбросили (вписывают свой ID) — тему не трогаем
        Assert.Equal("5", vm.TopicText);
    }

    // ---------- проверочное сообщение

    [Fact]
    public async Task Test_WithBadFields_SaysWhat_AndSendsNothing()
    {
        var vm = New(); // отправка не подменена: вызвать её — ошибка теста
        await vm.TestCommand.ExecuteAsync(null);
        AssertError(vm, "notify.tgNeedToken");
        Assert.Null(vm.Result); // проверка — не сохранение
    }

    [Fact]
    public async Task Test_SendsBuiltChannel_AndReports()
    {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyChannel? sentTo = null;
        NotifyMessage? sent = null;
        var vm = New(send: (c, m) =>
        {
            sentTo = c;
            sent = m;
            return reply.Task;
        });
        vm.Name = "Мой бот";
        vm.TelegramToken = Token;
        vm.ChatText = "-100777";

        var running = vm.TestCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("notify.testing", "Мой бот"), vm.StatusText);
        Assert.False(vm.StatusIsError);
        Assert.False(vm.TestCommand.CanExecute(null)); // пока шлём — кнопка выключена

        reply.SetResult();
        await running;

        Assert.Equal(Loc.T("notify.testSent", "Мой бот"), vm.StatusText);
        Assert.False(vm.StatusIsError);
        Assert.True(vm.TestCommand.CanExecute(null));
        Assert.NotNull(sentTo);
        Assert.Equal("-100777", sentTo.ChatId);
        Assert.Equal(Token, sentTo.Secret);
        Assert.Equal(Loc.T("notify.testTitle"), sent?.Title);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task Test_Error_IsShown()
    {
        var vm = New(send: (_, _) => throw new NotifyException("Бот не может писать в этот чат"));
        vm.TelegramToken = Token;
        vm.ChatText = "-100777";

        await vm.TestCommand.ExecuteAsync(null);

        Assert.Equal("Бот не может писать в этот чат", vm.StatusText);
        Assert.True(vm.StatusIsError);
    }

    // ---------- ответ окна

    [Fact]
    public void Save_Failing_LeavesNoResult()
    {
        var vm = New();
        var finished = 0;
        vm.Finished += _ => finished++;
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, finished);
        Assert.Null(vm.Result);
    }

    [Fact]
    public void Save_RaisesFinishedOnce_WithTheResult()
    {
        var vm = New();
        vm.TelegramToken = Token;
        vm.ChatText = "1";
        var finished = new List<NotifyChannel>();
        vm.Finished += finished.Add;
        vm.SaveCommand.Execute(null);
        Assert.Same(vm.Result, Assert.Single(finished));
    }
}
