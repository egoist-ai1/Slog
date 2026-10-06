using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Egoist.Voice.Core;
using Egoist.Voice.Services;

// Проект тянет и WPF, и WinForms ради иконки в трее, поэтому Brush и KeyEventArgs существуют в
// двух пространствах имён сразу. Псевдонимы фиксируют WPF-версии, как и в остальном коде.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
// HorizontalAlignment и VerticalAlignment внутри наследника Window перекрываются одноимёнными
// свойствами экземпляра, поэтому обращаться к перечислению можно только через отдельное имя.
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace Egoist.Voice;

/// <summary>
/// Режим начитки корпуса: <c>--corpus-record &lt;каталог корпуса&gt;</c>.
///
/// Без него корпус нечем записать — в README было сказано «наговорите фразы», и на этом всё,
/// поэтому корпус так и не появился, а вместе с ним не появилось и измерение точности.
/// </summary>
public partial class App
{
    private static readonly Brush RecorderBackground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D));
    private static readonly Brush RecorderText = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));
    private static readonly Brush RecorderMuted = new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x93));
    private static readonly Brush RecorderAccent = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x2F));
    private static readonly Brush RecorderDone = new SolidColorBrush(Color.FromRgb(0x4A, 0xC2, 0x6B));

    private async Task RunCorpusRecorderAsync(string corpusDirectory, string? profilePath)
    {
        try
        {
            corpusDirectory = Path.GetFullPath(corpusDirectory);
            var script = CorpusScript.Load(corpusDirectory);
            var profile = string.IsNullOrWhiteSpace(profilePath)
                ? null
                : CorpusBenchmarkProfile.Load(Path.GetFullPath(profilePath), script);
            var selectedIds = profile?.SelectedIds.ToHashSet(StringComparer.Ordinal);
            var lines = selectedIds is null
                ? script.Lines
                : script.Lines.Where(line => selectedIds.Contains(line.Id)).ToArray();
            if (lines.Count == 0)
            {
                MessageBox.Show(
                    $"В {CorpusScript.FileName} нет ни одной фразы.",
                    "Слог", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            var window = new CorpusRecorderWindow(script, lines, corpusDirectory, profile?.Id);
            window.Closed += (_, _) => Shutdown();
            window.Show();
            window.Activate();
            await Task.CompletedTask;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Corpus recorder failed type={exception.GetType().Name}");
            MessageBox.Show(
                exception.Message,
                "Слог", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// Окно начитки. Управление сведено к одной клавише: пробел удерживается на время фразы.
    /// При ста с лишним фразах каждое лишнее нажатие превращается в сотню лишних нажатий, поэтому
    /// подтверждения после записи нет — вместо него есть возврат на шаг назад.
    /// </summary>
    private sealed class CorpusRecorderWindow : Window
    {
        private readonly CorpusScript _script;
        private readonly IReadOnlyList<CorpusScriptLine> _lines;
        private readonly string _corpusDirectory;
        // Corpus recording is the one explicit mode allowed to persist WAV. Normal dictation uses
        // the same WASAPI path but remains memory-only.
        private readonly IAudioCaptureService _capture = new AudioCaptureService(persistCompletedTake: true);

        private readonly TextBlock _setTitle = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _setHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _progress = new() { FontSize = 12 };
        private readonly TextBlock _phrase = new() { FontSize = 24, TextWrapping = TextWrapping.Wrap, LineHeight = 34 };
        private readonly TextBlock _status = new() { FontSize = 13 };
        private readonly TextBlock _keys = new() { FontSize = 12 };
        private readonly Border _levelFill = new();

        private int _index;
        private bool _isRecording;
        private bool _isBusy;
        private string _currentSet = string.Empty;

        /// <summary>
        /// Потолок одного дубля. Самая длинная запись в скрипте — три минуты, так что четыре
        /// оставляют запас и при этом ловят зависший пробел прежде, чем он намолчит гигабайт.
        /// </summary>
        private static readonly TimeSpan MaxTakeDuration = TimeSpan.FromMinutes(4);

        private readonly DispatcherTimer _takeCeiling = new() { Interval = MaxTakeDuration };

        public CorpusRecorderWindow(
            CorpusScript script,
            IReadOnlyList<CorpusScriptLine> lines,
            string corpusDirectory,
            string? profileId)
        {
            _script = script;
            _lines = lines;
            _corpusDirectory = corpusDirectory;
            _index = 0;
            AdvancePastRecorded();

            Title = profileId is null
                ? "Слог — начитка корпуса"
                : $"Слог — начитка {profileId}";
            Width = 900;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = RecorderBackground;
            Foreground = RecorderText;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            _setTitle.Foreground = RecorderText;
            _setHint.Foreground = RecorderMuted;
            _progress.Foreground = RecorderMuted;
            _phrase.Foreground = RecorderText;
            _status.Foreground = RecorderMuted;
            _keys.Foreground = RecorderMuted;

            Content = BuildLayout();

            _capture.LevelChanged += OnLevelChanged;
            _takeCeiling.Tick += async (_, _) => await FinishTakeAsync(hitCeiling: true);
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            Closed += (_, _) =>
            {
                _takeCeiling.Stop();
                _capture.LevelChanged -= OnLevelChanged;
                _capture.Dispose();
            };

            Render();
        }

        private UIElement BuildLayout()
        {
            var levelTrack = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22)),
                HorizontalAlignment = HAlign.Stretch,
                Margin = new Thickness(0, 18, 0, 0)
            };
            _levelFill.Height = 4;
            _levelFill.CornerRadius = new CornerRadius(2);
            _levelFill.Background = RecorderAccent;
            _levelFill.HorizontalAlignment = HAlign.Left;
            _levelFill.Width = 0;
            levelTrack.Child = _levelFill;

            var grid = new Grid { Margin = new Thickness(40, 32, 40, 28) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_setTitle, 0);
            Grid.SetColumn(_progress, 1);
            header.Children.Add(_setTitle);
            header.Children.Add(_progress);

            _setHint.Margin = new Thickness(0, 6, 0, 0);
            _phrase.Margin = new Thickness(0, 28, 0, 0);
            _phrase.VerticalAlignment = VAlign.Center;
            _status.Margin = new Thickness(0, 14, 0, 0);
            _keys.Margin = new Thickness(0, 16, 0, 0);
            _keys.Text = "Пробел — держите, пока говорите   ·   Backspace — перезаписать предыдущую   ·   Esc — выйти, прогресс сохранён";

            Grid.SetRow(header, 0);
            Grid.SetRow(_setHint, 1);
            Grid.SetRow(_phrase, 2);
            Grid.SetRow(levelTrack, 3);
            Grid.SetRow(_status, 4);
            Grid.SetRow(_keys, 5);
            grid.Children.Add(header);
            grid.Children.Add(_setHint);
            grid.Children.Add(_phrase);
            grid.Children.Add(levelTrack);
            grid.Children.Add(_status);
            grid.Children.Add(_keys);
            return grid;
        }

        private void OnLevelChanged(object? sender, float level)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var available = Math.Max(0, ActualWidth - 80);
                _levelFill.Width = available * Math.Clamp(level, 0, 1);
            });
        }

        private void Render()
        {
            if (_index >= _lines.Count)
            {
                _setTitle.Text = "Готово";
                _setHint.Text = string.Empty;
                _phrase.Text = "Все фразы записаны. Можно закрывать окно и запускать замер.";
                _phrase.Foreground = RecorderDone;
                _progress.Text = $"{_lines.Count} из {_lines.Count}";
                _status.Text = string.Empty;
                _keys.Text = "Esc — выйти";
                return;
            }

            var line = _lines[_index];
            if (_script.Sets.TryGetValue(line.Set, out var set))
            {
                _setTitle.Text = set.Title;
                _setHint.Text = set.Hint;
            }
            else
            {
                _setTitle.Text = line.Set;
                _setHint.Text = string.Empty;
            }

            // Подсказка набора длинная и нужна только на его первой фразе; дальше она превращается
            // в шум, который читатель перестаёт замечать вместе с самой фразой.
            _setHint.Visibility = string.Equals(line.Set, _currentSet, StringComparison.Ordinal)
                ? Visibility.Collapsed
                : Visibility.Visible;

            _phrase.Text = line.Text;
            _phrase.Foreground = RecorderText;
            var recorded = _lines.Count(candidate =>
                File.Exists(Path.Combine(_corpusDirectory, candidate.Audio)));
            _progress.Text = $"{_index + 1} из {_lines.Count}   ·   записано {recorded}";
            _status.Text = File.Exists(Path.Combine(_corpusDirectory, line.Audio))
                ? "Эта фраза уже записана — пробел перезапишет её."
                : string.Empty;
        }

        private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (e.Key == Key.Back && !_isRecording && !_isBusy)
            {
                e.Handled = true;
                if (_index > 0)
                {
                    _index--;
                    _currentSet = _lines[_index].Set;
                    Render();
                }

                return;
            }

            // IsRepeat: удержание пробела шлёт поток KeyDown, и без этой проверки запись
            // перезапускалась бы десятки раз в секунду.
            if (e.Key != Key.Space || e.IsRepeat || _isRecording || _isBusy || _index >= _lines.Count)
            {
                return;
            }

            e.Handled = true;
            try
            {
                _capture.Start();
                _isRecording = true;
                _takeCeiling.Start();
                _status.Text = "Идёт запись…";
                _status.Foreground = RecorderAccent;
            }
            catch (Exception exception)
            {
                AppLog.Write($"Corpus recorder capture failed type={exception.GetType().Name}");
                _status.Text = "Микрофон недоступен: " + exception.Message;
                _status.Foreground = RecorderAccent;
            }

            await Task.CompletedTask;
        }

        private async void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !_isRecording)
            {
                return;
            }

            e.Handled = true;
            await FinishTakeAsync(hitCeiling: false);
        }

        /// <summary>
        /// Завершает дубль и сохраняет его. Вызывается и по отпусканию пробела, и по потолку
        /// длительности: событие отпускания теряется — например, если фокус ушёл, — и без потолка
        /// запись растёт молча. Один такой случай уже дал файл на сорок мегабайт вместо фразы.
        /// </summary>
        private async Task FinishTakeAsync(bool hitCeiling)
        {
            if (!_isRecording)
            {
                return;
            }

            _isRecording = false;
            _isBusy = true;
            _takeCeiling.Stop();
            _levelFill.Width = 0;

            try
            {
                var result = await _capture.StopAsync(CancellationToken.None);
                var line = _lines[_index];
                var target = Path.Combine(_corpusDirectory, line.Audio);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var capturedPath = result.Path ?? throw new InvalidDataException("Запись корпуса не создала WAV-файл.");
                File.Move(capturedPath, target, overwrite: true);
                WriteReference();

                _currentSet = line.Set;
                _index++;
                AdvancePastRecorded();
                Render();

                // Отказ гейта тишины сообщается, но фразу не блокирует: слишком тихая запись
                // остаётся в корпусе намеренно — она часть того, как человек реально говорит.
                if (hitCeiling)
                {
                    _status.Foreground = RecorderAccent;
                    _status.Text =
                        $"Запись оборвана по потолку в {MaxTakeDuration.TotalMinutes:0} мин — похоже, пробел завис. " +
                        "Вернитесь через Backspace и перезапишите.";
                }
                else
                {
                    _status.Foreground = result.HasSpeech ? RecorderDone : RecorderAccent;
                    _status.Text = result.HasSpeech
                        ? $"Записано {result.Duration.TotalSeconds:0.0} с, пик {result.PeakDecibels:0.0} дБ"
                        : "Записано, но речи почти не слышно — стоит перезаписать через Backspace";
                }
            }
            catch (Exception exception)
            {
                AppLog.Write($"Corpus recorder save failed type={exception.GetType().Name}");
                _status.Foreground = RecorderAccent;
                _status.Text = "Не удалось сохранить: " + exception.Message;
            }
            finally
            {
                _isBusy = false;
            }
        }

        private void AdvancePastRecorded()
        {
            while (_index < _lines.Count &&
                File.Exists(Path.Combine(_corpusDirectory, _lines[_index].Audio)))
            {
                _index++;
            }
        }

        /// <summary>
        /// Перестраивает reference.jsonl целиком после каждой записи. Записывается через временный
        /// файл: начитка идёт десятки минут, и обрыв на середине не должен превращать эталоны в
        /// обрезанный файл, из-за которого пропадёт весь предыдущий труд.
        /// </summary>
        private void WriteReference()
        {
            var path = Path.Combine(_corpusDirectory, CorpusBenchmark.ReferenceFileName);
            var temporary = path + ".tmp";

            // BOM намеренно: файл вычитывается руками, и его откроют Блокнотом или Get-Content,
            // а PowerShell 5.1 без BOM читает UTF-8 в кодировке ANSI и показывает кракозябры.
            // Читателям файла (File.ReadLines) BOM не мешает.
            File.WriteAllText(temporary, _script.BuildReference(_corpusDirectory), new UTF8Encoding(true));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
