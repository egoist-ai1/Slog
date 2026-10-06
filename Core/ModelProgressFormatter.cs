using Egoist.Voice.Services;

namespace Egoist.Voice.Core;

public static class ModelProgressFormatter
{
    public static RecognitionModelPresentation ControlCenter(
        bool allModelsReady,
        ModelTransferProgress? progress)
    {
        if (allModelsReady)
        {
            return new RecognitionModelPresentation(
                "GigaAM + Whisper · готовы к работе",
                IsFailure: false,
                ShowProgress: false,
                CanRetry: false);
        }

        if (progress?.Stage == ModelTransferStage.Failed)
        {
            return new RecognitionModelPresentation(
                $"Не удалось подготовить {progress.ModelName}. Повторите загрузку.",
                IsFailure: true,
                ShowProgress: false,
                CanRetry: true);
        }

        return new RecognitionModelPresentation(
            progress is null || progress.Stage == ModelTransferStage.Waiting
                ? "GigaAM + Whisper · ожидают подготовки"
                : Detail(progress),
            IsFailure: false,
            ShowProgress: true,
            CanRetry: false);
    }

    public static string Capsule(ModelTransferProgress progress)
    {
        var stage = progress.Stage switch
        {
            ModelTransferStage.Verifying => "Проверяю",
            ModelTransferStage.Loading => "Запускаю модель",
            ModelTransferStage.Ready => "Готово",
            ModelTransferStage.Failed => "Ошибка загрузки",
            _ => "Модель"
        };
        return progress.Stage == ModelTransferStage.Downloading
            ? $"Модель · {progress.Percentage:0}%"
            : progress.Stage is ModelTransferStage.Verifying or ModelTransferStage.Loading
                ? $"{stage} · {progress.Percentage:0}%"
                : stage;
    }

    public static string TrayTooltip(ModelTransferProgress progress)
    {
        var eta = progress.EstimatedRemaining is null ? string.Empty : $" · ~{FormatEta(progress.EstimatedRemaining.Value)}";
        return Truncate($"Слог · {progress.ModelName} {progress.Percentage:0}%{eta}", 63);
    }

    public static string Detail(ModelTransferProgress progress)
    {
        if (progress.Stage == ModelTransferStage.Failed)
        {
            return $"{progress.ModelName} · ошибка: {progress.Error}";
        }
        if (progress.Stage == ModelTransferStage.Ready)
        {
            return $"{progress.ModelName} · готово";
        }

        var speed = progress.BytesPerSecond > 0 ? $" · {FormatSpeed(progress.BytesPerSecond)}" : string.Empty;
        var eta = progress.EstimatedRemaining is null ? string.Empty : $" · ~{FormatEta(progress.EstimatedRemaining.Value)}";
        return $"{progress.ModelName} · {progress.Percentage:0}% · " +
               $"{FormatBytes(progress.BytesReceived)}/{FormatBytes(progress.TotalBytes)}{speed}{eta}";
    }

    public static string Overall(ModelTransferProgress progress) =>
        $"Модель {progress.ModelIndex}/{progress.ModelCount} · всего {progress.OverallPercentage:0}%";

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.0} ГБ";
        }
        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:0} МБ";
        }
        return $"{bytes / 1024d:0} КБ";
    }

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1_000_000
        ? $"{bytesPerSecond / 1_000_000d:0} МБ/с"
        : $"{bytesPerSecond / 1_000d:0} КБ/с";

    private static string FormatEta(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}

public sealed record RecognitionModelPresentation(
    string StatusText,
    bool IsFailure,
    bool ShowProgress,
    bool CanRetry);
