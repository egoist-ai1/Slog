namespace Egoist.Voice.Core;

/// <summary>
/// Versioned, conservative catalogue of names Egoist Voice can canonicalize locally.
/// </summary>
/// <remarks>
/// <para>
/// Every substitution is deterministic and whole-token bounded. Safe aliases are available in all
/// profiles; genuinely ambiguous forms are limited to an explicit technology or gaming context.
/// A missed brand is preferable to silently changing an ordinary Russian word.
/// </para>
/// <para>
/// Deliberately excluded as standalone global aliases: «хром», «курсор», «нода», «питон»,
/// «редис», «стим», «телега», «мета», «опера», «лама» and «сора». Multi-word forms below repair
/// only catalogue-backed whitespace/hyphen splits; there is no arbitrary edit-distance guessing.
/// </para>
/// </remarks>
public static class BuiltInVocabulary
{
    public const string Version = "8";

    // Audio-confirmed formatting only, never added to the unconditional user dictionary.
    // These consonant-stem names keep the exact Russian case ending supplied by both ASRs.
    internal static IReadOnlyDictionary<string, string[]> AudioConfirmedRussianNames { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Телеграм"] = ["телеграм", "тилеграм"],
            ["Дискорд"] = ["дискорд"],
            ["Гитхаб"] = ["гитхаб"],
            ["Гитлаб"] = ["гитлаб"],
            ["Ютуб"] = ["ютуб"]
        };

    // Exact uppercase corroboration is required. Ordinary lower/title-case words never activate it.
    internal static IReadOnlyList<string> AudioConfirmedAbbreviations { get; } =
        ["ИИ", "СССР", "РФ", "США", "МЧС"];

    /// <summary>
    /// The dictionary sorts aliases by length, so a longer entity always wins over a contained one.
    /// Canonical Latin aliases also repair casing without changing already-correct text.
    /// </summary>
    public static IReadOnlyList<DictionaryTerm> Terms { get; } =
    [
        // ── EGOIST product names reported by the user's own dictation ───────
        new(
            [
                "эгоист войс", "эгист войс", "егоист войс", "эгаист войс", "эгейст войс",
                "эгоист voice", "эгист voice", "егоист voice", "эгаист voice",
                "эгaist voice", "эгаist voice", "egoist voice", "egist voice", "egast voice"
            ],
            "Egoist Voice"),
        new(
            [
                "эгоист транслейтор", "эгист транслейтор", "эгаист транслейтор",
                "эгоист транслейт", "эгист транслейт", "эгаст транслейт",
                "эгоист translate", "эгист translate", "эгаст translate",
                "эгaist translate", "эгаist translate", "egoist translator", "egoist translate",
                "egist translate", "egast translate"
            ],
            "EGOIST Translator"),
        new(
            [
                "egoist games", "egoistgames", "egist games", "agist games", "agistgames",
                "эгоист геймс", "эгоист геймз", "эгист геймс", "эгаист геймс"
            ],
            "Egoist Games"),
        new(
            [
                "эгоист шилд", "эгист шилд", "эгаист шилд", "эгaist shield", "egoist shield", "egist shield",
                "агатхилд", "agathield", "агат шилд", "эгоист shield", "agathields",
                "эгоист шилт", "эгист шилт", "эгаист шилт", "егоист шилт", "егоист шилд",
                "эгaist шилт", "эгaist шилд", "эгоистшилт", "эгоистшилд", "агат шилт", "агатхилт"
            ],
            "Egoist Shield"),
        new(
            [
                "эгоист аккаунт менеджер", "эгоист акаунт менеджер", "эгоист-аскаунт-менеджер", "эгоист аскаунт менеджер",
                "эгоист аккаунт-менеджер", "эгоист акаунт-менеджер", "egoist-аскаунт-менеджер", "egoist-аккаунт-менеджер",
                "egoist account manager", "egoist account-manager", "эгоист аккаунтменеджер", "эгоист акаунтменеджер"
            ],
            "Egoist Account Manager"),
        new(
            [
                "эгоист аккаунт", "эгист аккаунт", "egoist account", "egoistaccount"
            ],
            "Egoist Account"),
        new(
            [
                "egoist codex", "egoistcodex", "эгоист кодекс", "эгоисткодекс", "эгист кодекс"
            ],
            "Egoist Codex"),

        // Complete place name and the exact mistaken output confirmed by the user.
        // Valid English "Rostov-on-Don" and other cities named Rostov remain unchanged.
        new(
            [
                "ростов на дону", "ростов-на-дону", "ростов надону", "rostofundone",
                "inrost on don", "inrost-on-don", "happy end inrost on don"
            ],
            "Ростов-на-Дону"),
        new(
            [
                "ростова на дону", "ростова-на-дону", "ростова надону"
            ],
            "Ростова-на-Дону"),
        new(
            [
                "ростове на дону", "ростове-на-дону", "ростове надону"
            ],
            "Ростове-на-Дону"),
        new(
            [
                "ростову на дону", "ростову-на-дону", "ростову надону"
            ],
            "Ростову-на-Дону"),
        new(["джунгарик"], "Джунгарик"),
        new(["джунгарики"], "Джунгарики"),

        // ── AI and local assistants ──────────────────────────────────────────
        new(["войс студио", "войс студия", "voicestudio"], "VoiceStudio"),
        new(["клод код", "клодкод", "клод коуд", "к лод код", "claude code"], "Claude Code"),
        new(["клод", "к лод", "claude"], "Claude"),
        new(["чат джипити", "чатджипити", "чат гпт", "чат джи пи ти", "chatgpt"], "ChatGPT"),
        new(["джемини", "гемини", "джеминай", "джи мини", "gemini"], "Gemini"),
        new(["копайлот", "копилот", "ко пайлот", "copilot"], "Copilot"),
        new(["опенай", "оупенэйай", "опен ай", "оупен ай", "openai"], "OpenAI"),
        new(["антропик", "антро пик", "энтропик", "anthropic"], "Anthropic"),
        new(["дипсик", "дип сик", "deapsik", "deepsik", "deepseek"], "DeepSeek"),
        new(["перплексити", "пер плексити", "perplexity"], "Perplexity"),
        new(["миджорни", "мид джорни", "midjourney"], "Midjourney"),
        new(["хаггинг фейс", "хагин фейс", "hugging face"], "Hugging Face"),
        new(["гигачат", "гига чат", "gigachat"], "GigaChat"),
        new(["гига ам", "гигаам", "gigaam"], "GigaAM"),
        new(["кьювен", "квен", "квэн", "qwen"], "Qwen"),
        new(["оллама", "ollama"], "Ollama"),
        new(["грок", "grok"], "Grok"),
        new(["стейбл диффьюжн", "stable diffusion"], "Stable Diffusion"),

        // These pronunciations collide with ordinary English/Russian wording. They become active
        // only in a detected or explicitly requested technology context.
        new(
            ["клауд код", "клаудкод", "cloud code"],
            "Claude Code",
            Profiles: EntityProfile.Technology,
            BlockWhenTextContains: ["google cloud", "гугл клауд", "cloud function", "облачн"]),
        new(["клауд"], "Claude", Profiles: EntityProfile.Technology),
        new(
            ["кодекс", "codex"],
            "Codex",
            Profiles: EntityProfile.Technology,
            BlockWhenTextContains: ["гражданск", "уголовн", "налогов", "правов", "чести", "законов"]),
        new(
            ["курсор", "cursor"],
            "Cursor",
            Profiles: EntityProfile.Technology,
            BlockWhenTextContains: ["постав", "перемест", "строк", "мыш", "указател", "позици"]),
        new(
            ["мета", "meta"],
            "Meta",
            Profiles: EntityProfile.Technology,
            BlockWhenTextContains: ["метадан", "анализ", "уровен", "ирони", "шутк"]),

        new(
            [
                "гитхаб", "гит хаб", "гидхаб", "гид хаб", "git hub", "github",
                "githap", "гитхап", "гит хап", "гид хап",
                "гетхаб", "гет хаб", "гедхаб", "гед хаб", "gethub", "get hub",
                "гетхап", "гет хап", "githab", "gethab"
            ],
            "GitHub"),
        new(
            [
                "astra terra", "astraterra", "астра терра", "астратерра", "астра тера", "астратера",
                "astrater", "астратер", "астра тэрра", "астратэрра", "астра-терра", "astra-terra"
            ],
            "Astra Terra"),
        new(["гитлаб", "гит лаб", "гетлаб", "гет лаб", "git lab", "gitlab", "getlab", "get lab"], "GitLab"),
        new(["битбакет", "бит бакет", "bitbucket"], "Bitbucket"),
        new(["телеграм", "теле грам", "telegram"], "Telegram"),
        new(["дискорд", "дис корд", "discord"], "Discord"),
        new(["слак", "slack"], "Slack"),
        new(["майкрософт тимс", "тимс", "microsoft teams"], "Microsoft Teams"),
        new(["зум", "zoom"], "Zoom"),
        new(["ноушен", "ноу шен", "notion"], "Notion"),
        new(["фигма", "фиг ма", "figma"], "Figma"),
        new(["канва", "canva"], "Canva"),
        new(["джира", "jira"], "Jira"),
        new(["конфлюэнс", "confluence"], "Confluence"),
        new(["линеар", "linear"], "Linear"),
        new(["сентри", "sentry"], "Sentry"),
        new(["страйп", "stripe"], "Stripe"),
        new(["ютуб", "ю туб", "youtube"], "YouTube"),
        new(["реддит", "ред дит", "reddit"], "Reddit"),
        new(["спотифай", "spotify"], "Spotify"),
        new(["гугл хром", "хром браузер", "google chrome"], "Google Chrome"),
        new(["файрфокс", "firefox"], "Firefox"),
        new(["майкрософт эдж", "эдж браузер", "microsoft edge"], "Microsoft Edge"),
        new(["опера браузер", "opera browser"], "Opera browser"),
        new(["брейв браузер", "brave browser"], "Brave browser"),
        new(["гугл драйв", "google drive"], "Google Drive"),
        new(["гугл", "гугль", "google"], "Google"),
        new(["ван драйв", "onedrive"], "OneDrive"),
        new(["дропбокс", "dropbox"], "Dropbox"),
        new(["майкрософт ворд", "ворд", "microsoft word"], "Microsoft Word"),
        new(["майкрософт эксель", "excel"], "Microsoft Excel"),
        new(["пауэрпоинт", "powerpoint"], "PowerPoint"),

        // ── Languages, runtimes and data ─────────────────────────────────────
        new(["пайтон", "python"], "Python"),
        new(["джаваскрипт", "джава скрипт", "java script", "javascript"], "JavaScript"),
        new(["тайпскрипт", "тайп скрипт", "type script", "typescript"], "TypeScript"),
        new(["джейсон", "джсон", "json"], "JSON"),
        new(["ямл", "яамл", "yaml"], "YAML"),
        new(["эйч ти эм эл", "html"], "HTML"),
        new(["си эс эс", "css"], "CSS"),
        new(["эс кью эл", "sql"], "SQL"),
        new(["эйч ти ти пи", "http"], "HTTP"),
        new(["эй пи ай", "апи", "api"], "API"),
        new(["реакт", "react"], "React"),
        new(["вью джиэс", "vue js", "vue.js"], "Vue.js"),
        new(["нэкст джиэс", "некст джиэс", "next js", "next.js"], "Next.js"),
        new(["ноуд джиэс", "нод джиэс", "node js", "node.js"], "Node.js"),
        new(["дотнет", "дот нет", "dotnet", ".net"], ".NET"),
        new(["си шарп", "c sharp", "c#"], "C#"),
        new(["си плюс плюс", "c plus plus", "c++"], "C++"),
        new(["раст ленг", "rust lang", "rust"], "Rust"),
        new(["гоу ленг", "go lang"], "Go"),
        new(["котлин", "kotlin"], "Kotlin"),
        new(["свифт ленг", "swift lang"], "Swift"),

        // ── Development, cloud and databases ────────────────────────────────
        new(["докер компоуз", "docker compose"], "Docker Compose"),
        new(["докер", "docker"], "Docker"),
        new(["кубернетес", "кубер нетес", "кубернетис", "kubernetes"], "Kubernetes"),
        new(["постгрескл", "постгрес", "пост грес", "postgresql"], "PostgreSQL"),
        new(["эс кью лайт", "sqlite"], "SQLite"),
        new(["май эс кью эл", "mysql"], "MySQL"),
        new(["монго ди би", "mongodb"], "MongoDB"),
        new(["графана", "гра фана", "grafana"], "Grafana"),
        new(["прометеус", "prometheus"], "Prometheus"),
        new(["терраформ", "терра форм", "terraform"], "Terraform"),
        new(["ансибл", "ansible"], "Ansible"),
        new(["дженкинс", "jenkins"], "Jenkins"),
        new(["энджин икс", "nginx"], "Nginx"),
        new(["эн пи эм", "npm"], "npm"),
        new(["верцел", "версел", "vercel"], "Vercel"),
        new(["клаудфлэр", "клауд флэр", "cloudflare"], "Cloudflare"),
        new(["супабейс", "супа бейс", "supabase"], "Supabase"),
        new(["файрбейс", "firebase"], "Firebase"),
        new(["неон", "neon"], "Neon"),
        new(["электрон", "electron"], "Electron"),
        new(["вайт", "vite"], "Vite"),
        new(["дабл ю пи эф", "wpf"], "WPF"),
        new(["эм ви ви эм", "mvvm"], "MVVM"),
        new(["вижуал студио код", "вижл студио код", "visual studio code"], "Visual Studio Code"),
        new(["вс код", "вэ эс код", "vs code"], "VS Code"),
        new(["вижуал студио", "visual studio"], "Visual Studio"),
        new(["андроид студио", "android studio"], "Android Studio"),
        new(["джетбрейнс", "jetbrains"], "JetBrains"),
        new(["интеллиджей идея", "intellij idea"], "IntelliJ IDEA"),
        new(["пайчарм", "pycharm"], "PyCharm"),
        new(["вебшторм", "webstorm"], "WebStorm"),
        new(["икскод", "xcode"], "Xcode"),
        new(["тестфлайт", "testflight"], "TestFlight"),
        new(["джетпак компоуз", "jetpack compose"], "Jetpack Compose"),
        new(["постман", "postman"], "Postman"),
        new(["сваггер", "свэггер", "swagger"], "Swagger"),
        new(["виндсерф", "виндсёрф", "windsurf"], "Windsurf"),
        new(["си ай си ди", "сиай сиди", "ci cd", "ci/cd", "cicd"], "CI/CD"),
        new(["пайплайн", "пайп лайн", "pipeline"], "pipeline"),
        new(["девопс", "дэвопс", "devops"], "DevOps"),
        new(["фуллстек", "фулстек", "фуллстэк", "фулстэк", "fullstack"], "fullstack"),
        new(["бенчмарк", "бэнчмарк", "benchmark"], "benchmark"),
        new(["деплой", "дэплой", "deploy"], "deploy"),
        new(["эс дэ ка", "сдк", "sdk"], "SDK"),
        new(["ай ди и", "ide"], "IDE"),
        new(["си эл ай", "cli"], "CLI"),
        new(["джи ю ай", "гуи", "gui"], "GUI"),
        new(["урл", "ю ар эл", "url"], "URL"),
        new(["днс", "ди эн эс", "dns"], "DNS"),
        new(["ай пи", "айпи", "ip"], "IP"),
        new(["впн", "ви пи эн", "vpn"], "VPN"),
        new(["ссх", "эс эс эйч", "ssh"], "SSH"),
        new(["ссл", "эс эс эл", "ssl"], "SSL"),
        new(["тлс", "tls"], "TLS"),

        // ── Operating systems, hardware and large companies ─────────────────
        new(["линукс", "linux"], "Linux"),
        new(["убунту", "ubuntu"], "Ubuntu"),
        new(["виндовс", "виндоус", "windows"], "Windows"),
        new(["макос", "мак ос", "macos"], "macOS"),
        new(["ай о эс", "ios"], "iOS"),
        new(["андроид", "android"], "Android"),
        new(["павершелл", "пауэршелл", "повершелл", "пауэр шелл", "powershell"], "PowerShell"),
        new(["энвидиа", "эн видиа", "нвидиа", "nvidia"], "NVIDIA"),
        new(["джифорс ртх", "джефорс ртх", "geforce rtx"], "GeForce RTX"),
        new(["джифорс гтх", "джефорс гтх", "geforce gtx"], "GeForce GTX"),
        new(["джифорс", "джефорс", "гефорс", "geforce"], "GeForce"),
        new(["ртх 5090", "ртх пятьдесят девяносто", "эр тэ икс 5090", "р т х 5090", "rtx 5090"], "RTX 5090"),
        new(["ртх 5080", "ртх пятьдесят восемьдесят", "эр тэ икс 5080", "р т х 5080", "rtx 5080"], "RTX 5080"),
        new(["ртх 4090", "ртх сорок девяносто", "эр тэ икс 4090", "р т х 4090", "rtx 4090"], "RTX 4090"),
        new(["ртх 4080", "ртх сорок восемьдесят", "эр тэ икс 4080", "р т х 4080", "rtx 4080"], "RTX 4080"),
        new(["ртх 4070", "ртх сорок семьдесят", "эр тэ икс 4070", "р т х 4070", "rtx 4070"], "RTX 4070"),
        new(["ртх 3080", "ртх тридцать восемьдесят", "эр тэ икс 3080", "р т х 3080", "rtx 3080"], "RTX 3080"),
        new(["ртх 3070", "ртх тридцать семьдесят", "эр тэ икс 3070", "р т х 3070", "rtx 3070"], "RTX 3070"),
        new(["ртх 3060", "ртх тридцать шестьдесят", "эр тэ икс 3060", "р т х 3060", "rtx 3060"], "RTX 3060"),
        new(["ртх", "эр тэ икс", "rtx"], "RTX"),
        new(["гтх", "джи ти икс", "gtx"], "GTX"),
        new(["эй эм ди", "amd"], "AMD"),
        new(["радеон", "радион", "radeon"], "Radeon"),
        new(["райзен", "райдзен", "ryzen"], "Ryzen"),
        new(["кор ай 9", "кор ай девять", "core i9"], "Core i9"),
        new(["кор ай 7", "кор ай семь", "core i7"], "Core i7"),
        new(["кор ай 5", "кор ай пять", "core i5"], "Core i5"),
        new(["кор ай 3", "кор ай три", "core i3"], "Core i3"),
        new(["интел", "intel"], "Intel"),
        new(["нвме ссд", "эн ви эм и ссд", "nvme ssd"], "NVMe SSD"),
        new(["нвме", "эн ви эм и", "nvme"], "NVMe"),
        new(["ссд", "эс эс ди", "эсэсди", "ssd"], "SSD"),
        new(["хдд", "эйч ди ди", "hdd"], "HDD"),
        new(["гпу", "джи пи ю", "gpu"], "GPU"),
        new(["цпу", "си пи ю", "cpu"], "CPU"),
        new(["биос", "bios"], "BIOS"),
        new(["уефи", "uefi"], "UEFI"),
        new(["тайп си", "тайпси", "type-c", "type c"], "Type-C"),
        new(["хдми", "эйч ди эм ай", "hdmi"], "HDMI"),
        new(["вай фай", "вайфай", "wi-fi", "wifi"], "Wi-Fi"),
        new(["блютуз", "блютус", "bluetooth"], "Bluetooth"),
        new(["асус", "asus"], "ASUS"),
        new(["эмсиай", "эм эс ай", "msi"], "MSI"),
        new(["асрок", "asrock"], "ASRock"),
        new(["логитек", "лоджитек", "logitech"], "Logitech"),
        new(["рейзер", "рэйзер", "razer"], "Razer"),
        new(["хайперикс", "хайпер икс", "hyperx"], "HyperX"),
        new(["стилсериес", "стил сериес", "steelseries"], "SteelSeries"),
        new(["фпс", "эф пэ эс", "fps"], "FPS"),
        new(["эпл", "apple"], "Apple"),
        new(["майкрософт", "майкро софт", "microsoft"], "Microsoft"),
        new(["адоби", "эдоби", "adobe"], "Adobe"),
        new(["амазон", "amazon"], "Amazon"),
        new(["эй дабл ю эс", "aws"], "AWS"),
        new(["эжур", "azure"], "Azure"),
        new(["самсунг", "samsung"], "Samsung"),
        new(["сони", "sony"], "Sony"),
        new(["оракл", "oracle"], "Oracle"),
        new(["ай би эм", "ibm"], "IBM"),
        new(["тесла", "tesla"], "Tesla"),
        new(["спейс икс", "spacex"], "SpaceX"),

        // ── Gaming, studios and creative applications ───────────────────────
        new(["эпик геймс стор", "epic games store"], "Epic Games Store"),
        new(["эпик геймс", "epic games"], "Epic Games"),
        new(["плейстейшен", "плей стейшен", "playstation"], "PlayStation"),
        new(["иксбокс", "икс бокс", "xbox"], "Xbox"),
        new(["нинтендо", "nintendo"], "Nintendo"),
        new(["вэлв", "valve"], "Valve"),
        new(["юбисофт", "ubisoft"], "Ubisoft"),
        new(["рокстар геймс", "rockstar games"], "Rockstar Games"),
        new(["близзард", "blizzard"], "Blizzard"),
        new(["активижн", "activision"], "Activision"),
        new(["райот геймс", "riot games"], "Riot Games"),
        new(["си ди проджект ред", "cd projekt red"], "CD Projekt Red"),
        new(["анриал энджин", "unreal engine"], "Unreal Engine"),
        new(["юнити", "unity"], "Unity"),
        new(["годо энджин", "godot engine"], "Godot Engine"),
        new(["майнкрафт", "minecraft"], "Minecraft"),
        new(
            [
                "path of exile 2", "path of exile two", "pathofexile2", "passfuizile2", "passive exile 2",
                "пас оф экзайл два", "пас оф экзайл 2", "пат оф экзайл два", "пат оф экзайл 2",
                "игрызай 2", "игрызай два", "игры зай 2", "игры зай два", "игрызайл 2", "игрызайл два",
                "игры зайл 2", "игры зайл два", "огрызай 2", "огрызай два", "огры зай 2", "огры зай два",
                "пасф экзайл 2", "пасф экзайл два", "пасфкзайл 2", "пас фкзайл 2", "пасфкзайл два", "пас фкзайл два",
                "басф экзайл 2", "басф экзайл два", "басс кзайл 2", "басс зайл 2", "басс экзайл 2", "бас экзайл 2",
                "басс ксайл 2", "бас ксайл 2", "пас кзайл 2", "пас ксайл 2",
                "bass xile 2", "pass of exile 2", "пас оф эксайл 2", "пас оф эксайл два",
                "poe 2", "poe2", "пое 2", "пое два", "пое2", "п о е 2"
            ],
            "Path of Exile 2"),
        new(
            [
                "path of exile", "пас оф экзайл", "пасофэкзайл", "басс экзайл", "бас экзайл", "басс зайл", "бас зайл"
            ],
            "Path of Exile"),
        new(
            [
                "другие игрызай 2", "другие игры зай 2", "другие огрызай 2", "другие огры зай 2"
            ],
            "другие игры: Path of Exile 2"),
        new(
            [
                "honor of kings", "хонор оф кингс", "онор оф кингс", "хонор оф кингз", "онор оф кингз",
                "conrov king says", "conrov king say", "конров кинг сейс", "конров кингс", "конров кинг",
                "хонор кингс", "хонорофкингс", "хонор оф кинг", "онор оф кинг"
            ],
            "Honor of Kings"),
        new(
            [
                "battlegrounds coach", "battleground coach", "батлграундс коуч", "батлграунд коуч",
                "батлграундс-коуч", "батлграунд-коуч"
            ],
            "Battlegrounds Coach"),
        new(["фортнайт", "fortnite"], "Fortnite"),
        new(["cs:go", "cs go", "ксго", "кс го"], "CS:GO"),
        new(["кс 2", "кс два", "кс2", "counter strike 2", "cs 2", "cs2"], "CS2"),
        new(["контр страйк", "counter strike", "counter-strike"], "Counter-Strike"),
        new(["дота два", "дота 2", "дота2", "дотка 2", "dota 2"], "Dota 2"),
        new(["лига легенд", "league of legends"], "League of Legends"),
        new(["валорант", "valorant"], "Valorant"),
        new(["дедлок", "дэдлок", "deadlock"], "Deadlock"),
        new(["балдурс гейт 3", "балдурс гейт", "балдурка", "бальдурс гейт 3", "bg3", "бг3", "baldur's gate 3"], "Baldur's Gate 3"),
        new(["хеллдайверс 2", "хеллдайверс", "хелдайверс 2", "helldivers 2"], "Helldivers 2"),
        new(["блэк миф вуконг", "блек миф вуконг", "вуконг", "black myth wukong", "black myth: wukong"], "Black Myth: Wukong"),
        new(["элден ринг", "элденринг", "елден ринг", "elden ring"], "Elden Ring"),
        new(["сталкер 2", "сталкер два", "s.t.a.l.k.e.r. 2", "stalker 2"], "S.T.A.L.K.E.R. 2"),
        new(["киберпанк двадцать семьдесят семь", "киберпанк 2077", "кибер панк 2077", "cyberpunk 2077"], "Cyberpunk 2077"),
        new(["гта 5", "гта пять", "гта v", "gta 5", "gta v"], "GTA 5"),
        new(["гта 6", "гта шесть", "gta 6"], "GTA 6"),
        new(["джи ти эй", "гта", "gta"], "GTA"),
        new(["кол оф дьюти", "колда", "call of duty"], "Call of Duty"),
        new(["варзон", "вар зона", "warzone"], "Warzone"),
        new(["апекс легендс", "апекс легендз", "апекс", "apex legends"], "Apex Legends"),
        new(["побег из таркова", "тарков", "escape from tarkov"], "Escape from Tarkov"),
        new(["вар тандер", "вар тандэр", "war thunder"], "War Thunder"),
        new(["ворлд оф танкс", "world of tanks"], "World of Tanks"),
        new(["ворлд оф варкрафт", "world of warcraft"], "World of Warcraft"),
        new(["батлнет", "батл нет", "battle.net", "battlenet"], "Battle.net"),
        new(["пс5", "п с пять", "ps5"], "PS5"),
        new(["нинтендо свитч", "нинтендо свич", "nintendo switch"], "Nintendo Switch"),
        new(["стим дек", "стимдэк", "steam deck"], "Steam Deck"),
        new(["ватсап", "вацап", "воцап", "вотсап мессенджер", "whatsapp"], "WhatsApp"),
        new(["гог гэлакси", "gog galaxy"], "GOG Galaxy"),
        new(["блендер", "blender"], "Blender"),
        new(["фотошоп", "photoshop"], "Photoshop"),
        new(["о би эс студио", "obs studio"], "OBS Studio"),
        new(["давинчи резолв", "davinci resolve"], "DaVinci Resolve"),
        new(["хд резка", "хдрезка", "hd резка", "hdрезка", "hd резко", "эйч ди резка", "hdrezka"], "HDRezka"),
        new(["лейзи медиа делюкс", "лейзимедиа делюкс", "lazy media deluxe", "lazymedia deluxe"], "LazyMedia Deluxe"),
        new(["торрсервер", "торр сервер", "torrserver", "torr server"], "TorrServer"),

        // Steam is profile-gated; the bounded case-ending grammar cannot consume «-ул» в «стимул».
        new(["стим", "steam"], "Steam", Profiles: EntityProfile.Gaming, BlockWhenTextContains: ["стимул"]),

        // ── Work vocabulary ──────────────────────────────────────────────────
        new(["пул реквест", "пулреквест", "пул-реквест"], "pull request"),
        new(["мердж реквест"], "merge request"),
        new(["код ревью", "кодревью"], "code review"),
        new(["эндпоинт", "энд поинт"], "endpoint"),
        new(["бэкенд", "бекенд"], "backend"),
        new(["фронтенд", "фронтэнд"], "frontend"),

        // ── Common misrecognitions and conversational fixes ──────────────────
        new(
            [
                "чепута текопа", "чепута тэкопа", "чипокута текапа", "типо кука текапа", "типо кука-текапа",
                "чепута текопы"
            ],
            "типа крутого сетапа"),

        // ── Spontaneous English expressions (Russian phonetics & Latin) ──────
        new(
            [
                "hello my friend how are you", "hello, my friend, how are you",
                "hello my friend how you", "hello, my friend, how you",
                "хелло май френд хау ар ю", "хэлло май френд хау ар ю",
                "хелло май френд хау ю", "хэлло май френд хау ю",
                "хеллоу май френд хау ар ю", "хэллоу май френд хау ар ю",
                "хелоу май френд хау ар ю", "хелоу май френд хау ю",
                "хелло май фрэнд хау ар ю", "хэлло май фрэнд хау ар ю",
                "хелло май фрэнд хау ю", "хэлло май фрэнд хау ю"
            ],
            "Hello, my friend, how are you"),
        new(
            [
                "hello my friend", "hello, my friend",
                "хелло май френд", "хэлло май френд",
                "хеллоу май френд", "хэллоу май френд",
                "хелоу май френд", "хелло май фрэнд", "хэлло май фрэнд"
            ],
            "Hello, my friend",
            BlockWhenTextContains: ["how", "хау"]),
        new(
            [
                "how are you doing", "how are you", "хау ар ю дуинг",
                "хау ар ю"
            ],
            "How are you?",
            BlockWhenTextContains: ["hello", "хелло", "хэлло", "хелоу"]),
        new(
            [
                "by the way", "бай зе вей", "бай зэ вэй", "бай зе вэй", "бай зэ вей", "байзевей"
            ],
            "by the way"),
        new(
            [
                "just in case", "джаст ин кейс", "джастин кейс", "джаст инкейс", "джаст ин кэйс"
            ],
            "just in case"),
        new(
            [
                "check this out", "чек зис аут", "чек зэ аут", "чек дис аут"
            ],
            "check this out"),
        new(
            [
                "let's go", "lets go", "летс гоу", "летс го", "лэтс гоу", "лэтс го"
            ],
            "Let's go"),
        new(
            [
                "thank you so much", "thank you very much",
                "сенк ю соу мач", "сэнк ю соу мач", "сенкью со мач", "сэнк ю со мач", "сенк ю со мач",
                "сенк ю вери мач", "сэнк ю вери мач", "сенкью вери мач"
            ],
            "Thank you so much"),
        new(
            [
                "thank you", "сенк ю", "сэнк ю", "сенкью"
            ],
            "Thank you"),
        new(
            [
                "you're welcome", "you are welcome", "юр велкам", "ю ар велкам", "ю а велкам"
            ],
            "You're welcome"),
        new(
            [
                "good luck", "гуд лак", "гудлак"
            ],
            "Good luck"),
        new(
            [
                "never mind", "nevermind", "невер майнд", "невэр майнд", "невермайнд"
            ],
            "Never mind"),
        new(
            [
                "looks good to me", "лукс гуд ту ми", "лукс гуд туми"
            ],
            "looks good to me"),
        new(
            [
                "that makes sense", "дет мейкс сенс", "дат мейкс сенс", "зэт мейкс сенс", "зет мейкс сенс", "дэт мэйкс сэнс"
            ],
            "that makes sense"),
        new(
            [
                "make sense", "makes sense", "мейк сенс", "мэйк сэнс", "мейкс сенс"
            ],
            "makes sense"),
        new(
            [
                "step by step", "степ бай степ", "стэп бай стэп"
            ],
            "step by step"),
        new(
            [
                "from scratch", "фром скретч", "фром скрэтч"
            ],
            "from scratch"),
        new(
            [
                "from russia with love", "фром раша виз лав", "фром раша визлав"
            ],
            "from Russia with love"),
        new(
            [
                "from russia", "фром раша", "фром рашша"
            ],
            "from Russia"),
        new(
            [
                "no problem", "no problems", "ноу проблем", "ноу проблемс"
            ],
            "no problem"),
        new(
            [
                "to be honest", "ту би хонест", "ту би онест", "ту би хонэст"
            ],
            "to be honest"),
        new(
            [
                "good job", "гуд джоб", "гуджоб"
            ],
            "Good job"),
        new(
            [
                "well done", "вел дан", "вэл дан"
            ],
            "Well done"),
        new(
            [
                "see you later", "си ю лейтер", "си ю лэйтер"
            ],
            "See you later"),
        new(
            [
                "take care", "тейк кер", "тэйк кэр"
            ],
            "Take care"),
        new(
            [
                "one more thing", "ван мор синг", "ван мор тинг"
            ],
            "one more thing"),
        new(
            [
                "as soon as possible", "ас сун ас посибл", "ас сун аз посибл"
            ],
            "as soon as possible"),
        new(
            [
                "are you sure", "ар ю шур", "а ю шур"
            ],
            "Are you sure"),
        new(
            [
                "i don't know", "i dont know", "ай донт ноу", "ай донт но"
            ],
            "I don't know"),
        new(
            [
                "i have no idea", "ай хэв ноу айдиа", "ай хев ноу айдиа"
            ],
            "I have no idea"),
        new(
            [
                "of course", "оф корс", "офкорс"
            ],
            "of course"),
        new(
            [
                "have a nice day", "хэв э найс дей", "хев э найс дей"
            ],
            "Have a nice day"),
        new(
            [
                "best regards", "бест регардс", "бэст регардс"
            ],
            "Best regards"),
        new(
            [
                "keep in touch", "кип ин тач"
            ],
            "keep in touch"),
        new(
            [
                "клауд кот"
            ],
            "Claude Code"),
        new(
            [
                "cloudfire", "cloud fire", "cloudfair"
            ],
            "Cloudflare"),
        new(
            [
                "redem", "readme file", "ридми файл"
            ],
            "README"),
        new(
            [
                "джессон", "джисон"
            ],
            "JSON"),
        new(
            [
                "ридми", "ридем", "ридмэ"
            ],
            "README"),
        new(
            [
                "ченчлок", "чейнджлог", "ченджлог"
            ],
            "CHANGELOG"),
        new(
            [
                "лиценс"
            ],
            "LICENSE"),
        new(
            [
                "кибернетис"
            ],
            "Kubernetes"),
        new(
            [
                "джавоскрипт"
            ],
            "JavaScript"),
        new(
            [
                "эскьюэль"
            ],
            "SQL"),
        new(
            [
                "эскьюлайт"
            ],
            "SQLite"),
        new(
            [
                "постгрес кьюэл", "постгрескьюэл", "пострегейс кьюэл", "постригей эскьюл", "постгре эскьюэль"
            ],
            "PostgreSQL"),
        new(
            [
                "джетпак компоус", "джетпэк компоус", "джетпак композ"
            ],
            "Jetpack Compose"),
        new(
            [
                "тест флайт", "тест-флайт"
            ],
            "TestFlight"),
        new(
            [
                "анриал лэнджинс", "анриал инджин", "анрил энджин"
            ],
            "Unreal Engine"),
        new(
            [
                "эпикгеймстор"
            ],
            "Epic Games Store"),
        new(
            [
                "версал"
            ],
            "Vercel"),
        new(
            [
                "клоудфаер", "клаудфлер", "клоудфлер", "клаудфлэйр"
            ],
            "Cloudflare"),
        new(
            [
                "супобейс", "супабэйс"
            ],
            "Supabase"),
        new(
            [
                "гитхав"
            ],
            "GitHub"),
        new(
            [
                "гитхав копайлет", "гитхаб копайлет"
            ],
            "GitHub Copilot"),
        new(
            [
                "давинчи изолф", "давинчи ризолв"
            ],
            "DaVinci Resolve"),
        new(
            [
                "обс студия", "обс студио"
            ],
            "OBS Studio"),
        new(
            [
                "повершел"
            ],
            "PowerShell"),
        new(
            [
                "визуал студия код", "визуал студио код"
            ],
            "Visual Studio Code"),
        new(
            [
                "но джиэс", "ноджс", "нод джей эс"
            ],
            "Node.js"),
        new(
            [
                "некстиэс", "некст джс", "некстджс"
            ],
            "Next.js"),
        new(
            [
                "амд"
            ],
            "AMD"),
        new(
            [
                "нвидия"
            ],
            "NVIDIA"),
        new(
            [
                "докер композ"
            ],
            "Docker Compose"),
        new(
            [
                "иксход", "экскод"
            ],
            "Xcode"),
        new(
            [
                "айос"
            ],
            "iOS"),
        new(
            [
                "опэн эй ай", "опен эй ай", "оупен эй ай", "опенэйай", "опэн ай"
            ],
            "OpenAI")
    ];

    /// <summary>Safe general forms used by the conditional mixed-speech detector.</summary>
    public static IEnumerable<string> SpokenForms => Terms
        .Where(term => (term.Profiles & EntityProfile.General) != 0)
        .SelectMany(term => term.Spoken ?? []);
}
