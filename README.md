# AITrans

AITrans е AI-базирано приложение за превод, изградено с [Avalonia UI](https://avaloniaui.net/). Поддържа превод на Markdown документи, SRT субтитри, импорт на e-книги (EPUB/FB2), flashcards за учене на думи и AI чат — всичко през избран от теб AI доставчик или DeepL.

## Съдържание

- [Функционалности](#функционалности)
- [Поддържани платформи](#поддържани-платформи)
- [Структура на проекта](#структура-на-проекта)
- [Изисквания](#изисквания)
- [Стартиране](#стартиране)
  - [Desktop (Windows/macOS/Linux)](#desktop-windowsmacoslinux)
  - [Browser (WebAssembly)](#browser-webassembly)
  - [Android](#android)
  - [iOS](#ios)
- [Настройка на AI доставчици](#настройка-на-ai-доставчици)
- [Известни ограничения по платформи](#известни-ограничения-по-платформи)

## Функционалности

- **Markdown** — отваряне/редактиране на `.md` файлове, превод по параграфи или на цял документ, YAML front matter редактор, search & replace, кеширане на превода.
- **Preview** — страниран преглед на преведен Markdown с вграден AI чат асистент (обобщаване, обяснение, превод на избран текст), text-to-speech четене.
- **Subtitles** — импорт/превод/експорт на `.srt` файлове, ред по ред или на партиди.
- **Flashcards & Memory** — карти за учене на думи (дума / превод / употреба), AI генериране на съдържание, игра на запомняне, TTS произношение.
- **AI Chat** — свободен чат с избрания AI модел, с поддръжка на Markdown рендиране на отговорите.
- **Import на е-книги** — EPUB/FB2 → Markdown, с извличане на изображения; експорт обратно в EPUB.
- **Text-to-Speech** — през Microsoft Cognitive Services Speech SDK (Desktop/Android; не е наличен в Browser build).

## Поддържани платформи

| Платформа | Проект | Статус |
|---|---|---|
| Desktop (Windows/macOS/Linux) | `AITrans.Desktop` | Пълна функционалност |
| Browser (WebAssembly) | `AITrans.Browser` | Работи; виж [ограниченията](#известни-ограничения-по-платформи) |
| Android | `AITrans.Android` | Компилира се в APK; виж [ограниченията](#известни-ограничения-по-платформи) |
| iOS | `AITrans.iOS` | Скеле от Avalonia темплейта, не е тествано |

## Структура на проекта

```
AITrans.sln
├── AITrans/            # Споделена логика, ViewModels, Views, Services (общо за всички платформи)
├── AITrans.Desktop/     # Desktop entry point (Windows/macOS/Linux)
├── AITrans.Browser/     # WebAssembly entry point
├── AITrans.Android/     # Android entry point
├── AITrans.iOS/         # iOS entry point (нетествано)
└── Directory.Packages.props  # Централизирано управление на NuGet версии
```

## Изисквания

- [.NET SDK 9.0+](https://dotnet.microsoft.com/download) (тествано с 9.0 и 10.0 SDK)
- За **Browser**: workloads `wasm-tools` и `wasm-tools-net9`
  ```powershell
  dotnet workload install wasm-tools wasm-tools-net9
  ```
- За **Android**: workload `android`, JDK 17+, Android SDK (platform-tools, platform 35/36, build-tools)
  ```powershell
  dotnet workload install android
  ```
- За **iOS**: Xcode на macOS (нетествано в този репозиторий)

## Стартиране

### Desktop (Windows/macOS/Linux)

```powershell
dotnet run --project AITrans.Desktop
```

### Browser (WebAssembly)

```powershell
dotnet run --project AITrans.Browser
```

Отваря локален dev сървър (URL-ът се извежда в конзолата, напр. `http://127.0.0.1:xxxxx/`).

> За production build с линкнати нативни библиотеки (SkiaSharp, HarfBuzzSharp, SQLite) `AITrans.Browser.csproj` вече има `<WasmBuildNative>true</WasmBuildNative>` — изисква инсталирания `wasm-tools-net9` workload.

### Android

```powershell
dotnet build AITrans.Android -c Debug -p:AndroidSdkDirectory="<път до Android SDK>" -p:JavaSdkDirectory="<път до JDK>"
```

Генерираният APK е в `AITrans.Android/bin/Debug/net9.0-android/*-Signed.apk`. За инсталиране на свързано устройство:

```powershell
adb install -r AITrans.Android/bin/Debug/net9.0-android/com.CompanyName.AITrans-Signed.apk
```

### iOS

Изисква Xcode и macOS машина — не е тествано в тази среда.

## Настройка на AI доставчици

Настройките (API ключове, избран модел, тема и др.) се задават от таб **Settings** в приложението и се пазят локално (DPAPI-криптирани на Windows). Поддържани доставчици за превод/чат:

- OpenAI
- GitHub Copilot
- OpenRouter
- Google Gemini
- DeepSeek
- Groq
- Ollama / LM Studio (локален модел)
- Qwen
- DeepL (отделен toggle за превод на Markdown/Subtitles, вкл. безплатен DeepL endpoint без API ключ)

## Известни ограничения по платформи

- **Text-to-Speech** (Microsoft Cognitive Services Speech SDK) няма native билд за `browser-wasm` — четенето на глас не работи в Browser версията.
- **Отваряне/запис на файлове** в Browser (и частично Android с `content://` URI-та) минава през stream-based `IStorageFile` API вместо директни пътеки — работи за Markdown/Subtitles/Preview, но **EPUB/FB2 import-export и Flashcard CSV все още разчитат на директни файлови пътеки** и не са напълно пренесени.
- Диалозите **История на файлове**, **История на кеш**, **YAML заглавие** и **Редактор на всички карти** използват отделен `Window`, който не се поддържа на Browser/Android (single-view платформи) — на тези платформи показват съобщение "не е налично на тази платформа" вместо да се отварят.
- Следването на относителни Markdown линкове (`[текст](друг.md)`) и "Go Back" историята в Preview разчитат на истинска файлова система — работят на Desktop, деградират грациозно (без срив) на Browser/Android.
