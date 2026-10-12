# Сборка и состав поставки

[← На главную](../README.md)

Нужны Windows x64 и .NET 8 SDK. Сборка приложения не скачивает модели речи или текста.

```powershell
dotnet restore Egoist.Voice.sln
dotnet test Egoist.Voice.sln -c Release
dotnet publish Egoist.Voice.csproj -c Release -r win-x64 --self-contained true -o artifacts/app
```

Клиент общего движка перевода закреплён в `vendor/translation-client/1.0.0`.
Его бинарные файлы и SHA-256 включены в репозиторий. Для запуска перевода нужен сам движок
из Full, но для сборки приложения соседний проект Translator не нужен.

## Compact

`scripts/Build-CompactPortable.ps1` публикует редакцию `VoiceFlavor=Compact`, проверяет
четыре GigaAM-файла по каталогу и копирует их из локального кэша. Скрипт не скачивает модели.
Маркер `egoist-voice.portable` включает явные переносимые пути: `Models` и `Data` рядом с EXE.
Установщик строит `scripts/Build-CompactInstaller.ps1` по полному списку файлов и SHA-256.

```powershell
./scripts/Build-CompactPortable.ps1 -OutputDirectory "$PWD/artifacts/compact/portable-stage"
./scripts/Build-CompactInstaller.ps1 -StagingDirectory "$PWD/artifacts/compact/portable-stage" -Build
```

Перед публикацией добавьте актуальные лицензии в staging и обновите manifest. Выходная
папка должна быть чистой: не копируйте в неё Data, словарь, аудио или личные настройки.

## Проверка

`dotnet test` проверяет программные контракты. Native-команды `--local-asr-check` и
`--local-qwen-check` проверяют модели; в отчётах не сохраняются распознанные тексты.
Установщики проверяйте только в Windows Sandbox/VM: установка, запуск без сети, repair,
сохранение данных и удаление. На рабочей машине сборочный скрипт установщик не запускает.

Автоматические тесты не заменяют корпус записей, проверку микрофонов и матрицу GPU/Windows.
Синтетические фразы не подтверждают общую точность речи. Финальная публикация 2.2.1
выполнена по явному решению владельца с сохранением ограничений в release notes.

## Russian 2.2.1

`scripts/Build-RussianInstaller.ps1` создаёт автономный русский комплект: Compact/GigaAM
и self-contained .NET 8.0.30. Qwen и её runtime по умолчанию не включены. Whisper,
Hy-MT и общий движок перевода не входят. Сначала восстановите Compact `win-x64`
с `-p:RuntimeFrameworkVersion=8.0.30`. Передайте явные локальные пути параметрам
`SpeechModelsRoot` и собственный
временный `WorkDirectory`. `OutputDirectory` должен быть новым каталогом в `artifacts`.
Без `-Build` скрипт показывает план; с ним проверяет входные SHA-256, публикует приложение,
создаёт Inno payload и один внешний EXE с проверяемыми вложениями. Модели не скачиваются.
Для необязательного текстового редактора передайте `-IncludeTextEditor` вместе с
`TextModelPath`, `TextRuntimeZip`, `VcRuntimeDirectory`. Даже такой пакет запускается
в режиме «Дословно»; автоматический запуск Qwen требует явного выбора другого режима.

`russian-payload.manifest.json` содержит полный состав, SHA-256 и ревизию исходников;
`russian-installer.json` — размер, подпись и результат проверки встроенного пакета.
Свежая установка выбирает «Дословно» и выключает историю аудио. Существующий
`Data/dictation.json` не заменяется и не удаляется деинсталлятором. Блокировку файлов
обрабатывает Windows Restart Manager; глобального `taskkill /IM` в этом установщике нет.
Сборка и проверка целостности не означают, что пройдены установка/обновление/удаление
в чистой Windows. Состав и ограничения финального релиза: [2.2.1](releases/2.2.1.md).

## Russian RNNT 2.3.0

Один CPU plain-RNNT профиль; точные четыре файла и ревизия в docs/models/gigaam-v3-rnnt.json. Примените актуальный SHA-bound Models/Speech/id/file layout. Сначала dotnet restore Egoist.Voice.csproj -r win-x64 -p:VoiceFlavor=Compact -p:RuntimeFrameworkVersion=8.0.30. После commit чистых reviewed sources запустите Build-RussianInstaller.ps1 с явными OutputDirectory, своим task WorkDirectory, SpeechModelsRoot и -Build. Сборка не скачивает модели и не устанавливает приложение. Внешний bootstrap и Inno работают без React/Electron. Финальный russian-payload.manifest.json содержит sourceDirty=false, версию и полный состав после добавления лицензий; именно его передавайте transactional updater. Dev bin после чередования Full/Compact не является release payload; проверяйте финальный publish с portable marker.