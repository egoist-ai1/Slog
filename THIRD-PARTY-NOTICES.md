# Сторонние компоненты

Egoist Voice распространяется вместе с речевыми моделями и нативными библиотеками сторонних
авторов. Ниже перечислено всё, что попадает в установщик или в сборку, с указанием лицензии.

Исходный код самого Egoist Voice лицензирован отдельно — см. `LICENSE`.

Архивный установщик 2.2.1 дополнительно включает React/React DOM 19.2.8 (MIT) и Electron
44.2.0 (MIT и лицензии включённых компонентов Chromium/Node). Лицензия React
находится в архиве приложения установщика, лицензии Electron/Chromium поставляются
с его runtime. Эти компоненты не входят в установленный Voice.
Источники: https://github.com/facebook/react и https://github.com/electron/electron.

Компактная версия Portable RU включает GigaAM, sherpa-onnx, ONNX Runtime,
NAudio и .NET. Нативные CUDA, Vulkan и движок перевода в неё не входят.
Текстовая Qwen устанавливается отдельно от Portable; источник закреплённой модели:
https://huggingface.co/Qwen/Qwen3-4B-GGUF/tree/bc640142c66e1fdd12af0bd68f40445458f3869b

---

## Речевые модели (входят в установщик)

### GigaAM v3 — основной русский движок

- Автор: Salute Developers (СберБанк)
- Источник: https://github.com/salute-developers/GigaAM
- Веса в формате sherpa-onnx: https://huggingface.co/Smirnov75/GigaAM-v3-sherpa-onnx
- Лицензия: **MIT**

Русская поставка 2.3.0 использует plain RNNT: `gigaam_v3_rnnt_encoder_int8.onnx`, `..._decoder.onnx`, `..._joint.onnx` и
`..._tokens.txt` рядом с EXE в `Models/Speech`; все четыре файла и исходная ревизия закреплены в model manifest. Исторический Full Preview 2 сохраняет свой
кэш при удалении приложения. Compact хранит модели рядом с EXE.

Русская поставка 2.4.0 дополнительно включает четыре hash-pinned E2E RNNT файла из той же ревизии `6888903da215c7735f51101d939f3bfa679fb2b8`. Plain RNNT определяет слова; E2E распознаёт то же аудио для пунктуации, регистра и подтверждения известных названий. Общий размер восьми файлов — 650 090 519 байт. Полный MIT notice модели и model card находятся в `Models/Licenses`.

Managed bindings sherpa-onnx включены как исходный UTF-8 fork `Egoist.Sherpa.Onnx.Utf8` 1.13.4.1, Apache-2.0, из commit `142807252687d81b40d6315f23470a1512a00de3`. Изменены Windows-маршалинг строк и проверки нулевых native handles; исходный публичный API сохранён. Native CAPI — официальный `sherpa-onnx-v1.13.4-win-x64-shared-MT-Release-no-tts-lib.tar.bz2`, SHA256 `9fd2bdb7fca85120e1e2099acb775c0079bdc1cd4c8d3de9a9eaee86394eb83e`. Синтез речи отключён; исходный GigaAM frontend сохранён. ONNX Runtime 1.27.0 берётся из hash-pinned runtime.win-x64 1.13.4; NuGet CAPI исключён. Native manifest и полные сторонние уведомления находятся в `vendor/sherpa-native-asr/1.13.4` и поставляются в `licenses/native-asr`. Исходники, patch и pinned manifest доступны в `vendor/sherpa-managed-utf8/1.13.4`; LICENSE, patch и manifest поставляются в `licenses/`. Экспериментальный исправленный frontend не входит в эту поставку.


---

## Библиотеки

| Компонент | Назначение | Лицензия | Источник |
|---|---|---|---|
| sherpa-onnx (`org.k2fsa.sherpa.onnx`) | рантайм GigaAM | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime | исполнение ONNX-моделей | MIT | https://github.com/microsoft/onnxruntime |
| NAudio | захват звука | MIT | https://github.com/naudio/NAudio |
| .NET 8 | среда исполнения | MIT | https://github.com/dotnet/runtime |
| Inno Setup | сборка установщика | модифицированная BSD | https://jrsoftware.org/isinfo.php |

---

## Шрифты и иконки

Интерфейс использует системные шрифты Windows (`Segoe UI Variable`, `Segoe UI`). Они не входят в
установщик. Все иконки капсулы нарисованы векторно в коде приложения и не заимствованы.

---

## Тексты лицензий

Полные тексты MIT и Apache-2.0:

- MIT: https://opensource.org/license/mit
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0

Полные тексты лицензий и copyright-уведомления включены в каталог `licenses/`.
MIT-лицензия исходников Egoist Voice не заменяет отдельные условия сторонних компонентов.

## Дополнения Full Preview 2

- **Qwen3-4B Q4_K_M**, Apache-2.0: официальный GGUF из указанного выше закреплённого revision.
  Текст лицензии — `licenses/Qwen3-4B-APACHE-2.0.txt`.
- **Hy-MT2-1.8B Q8_0**, Apache-2.0: модель перевода Tencent, revision
  `1cd5208700acedef4ef93019b6cfc148b8522d45`. `licenses/Hy-MT2-LICENSE.txt`.
- **llama.cpp b10219**, MIT: `licenses/llama.cpp-LICENSE.txt`.
- **Visual C++ 2022 Redistributable**, условия Microsoft: указатель на официальный список
  распространяемых компонентов — `licenses/Microsoft-Visual-Cpp-2022-REDIST.txt`.

Установщик Full сохраняет словарь, настройки, историю и кэш ASR/Qwen при обновлении и удалении.
Общий движок перевода учитывает владельцев Voice/Translator; чужой owner не удаляется.
