using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace Egoist.Voice.Services;

internal static class EgoistTrayPalette
{
    internal static Color Background { get; private set; } = Color.FromArgb(5, 5, 5);
    internal static Color Hover { get; private set; } = Color.FromArgb(29, 40, 9);
    internal static Color HoverBorder { get; private set; } = Color.FromArgb(92, 148, 0);
    internal static Color Primary { get; private set; } = Color.FromArgb(247, 247, 248);
    internal static Color Disabled { get; private set; } = Color.FromArgb(112, 112, 120);
    internal static Color Accent { get; private set; } = Color.FromArgb(168, 255, 0);
    internal static Color Separator { get; private set; } = Color.FromArgb(42, 42, 48);

    internal static void Apply(EffectiveAppTheme theme)
    {
        if (theme == EffectiveAppTheme.HighContrast)
        {
            Background = SystemColors.Window;
            Hover = SystemColors.Highlight;
            HoverBorder = SystemColors.HighlightText;
            Primary = SystemColors.WindowText;
            Disabled = SystemColors.GrayText;
            Accent = SystemColors.Highlight;
            Separator = SystemColors.WindowText;
            return;
        }

        if (theme == EffectiveAppTheme.Light)
        {
            Background = Color.FromArgb(246, 246, 248);
            Hover = Color.FromArgb(226, 242, 185);
            HoverBorder = Color.FromArgb(120, 176, 0);
            Primary = Color.FromArgb(24, 24, 27);
            Disabled = Color.FromArgb(112, 113, 122);
            Accent = Color.FromArgb(74, 130, 0);
            Separator = Color.FromArgb(207, 207, 215);
            return;
        }

        Background = Color.FromArgb(5, 5, 5);
        Hover = Color.FromArgb(29, 40, 9);
        HoverBorder = Color.FromArgb(92, 148, 0);
        Primary = Color.FromArgb(247, 247, 248);
        Disabled = Color.FromArgb(112, 112, 120);
        Accent = Color.FromArgb(168, 255, 0);
        Separator = Color.FromArgb(42, 42, 48);
    }
}

/// <summary>
/// Draws every tray-menu state explicitly so nested drop-downs never inherit
/// the Windows light renderer or its blue selection color.
/// </summary>
internal sealed class EgoistTrayRenderer : Forms.ToolStripProfessionalRenderer
{
    internal EgoistTrayRenderer()
        : base(new EgoistTrayColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(EgoistTrayPalette.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(EgoistTrayPalette.Separator);
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        using var background = new SolidBrush(e.Item.Selected
            ? EgoistTrayPalette.Hover
            : EgoistTrayPalette.Background);
        e.Graphics.FillRectangle(background, bounds);

        if (!e.Item.Selected)
        {
            return;
        }

        using var border = new Pen(EgoistTrayPalette.HoverBorder);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? EgoistTrayPalette.Primary
            : EgoistTrayPalette.Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var rect = e.ImageRectangle;
        var scale = Math.Max(1f, e.Graphics.DpiX / 96f);
        var centerX = rect.Left + (rect.Width / 2f);
        var centerY = rect.Top + (rect.Height / 2f);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(EgoistTrayPalette.Accent, 1.75f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawLines(pen,
        [
            new PointF(centerX - (4.5f * scale), centerY),
            new PointF(centerX - (1.2f * scale), centerY + (3.2f * scale)),
            new PointF(centerX + (5.2f * scale), centerY - (4.2f * scale))
        ]);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        var scale = Math.Max(1f, e.Graphics.DpiX / 96f);
        var centerX = e.ArrowRectangle.Left + (e.ArrowRectangle.Width / 2f);
        var centerY = e.ArrowRectangle.Top + (e.ArrowRectangle.Height / 2f);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item?.Enabled != false ? EgoistTrayPalette.Primary : EgoistTrayPalette.Disabled, 1.2f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawLines(pen,
        [
            new PointF(centerX - (2f * scale), centerY - (3f * scale)),
            new PointF(centerX + (1f * scale), centerY),
            new PointF(centerX - (2f * scale), centerY + (3f * scale))
        ]);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(EgoistTrayPalette.Separator);
        e.Graphics.DrawLine(pen, 12, y, Math.Max(12, e.Item.Width - 12), y);
    }

    private sealed class EgoistTrayColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => EgoistTrayPalette.Background;
        public override Color MenuItemSelected => EgoistTrayPalette.Hover;
        public override Color MenuItemBorder => EgoistTrayPalette.HoverBorder;
        public override Color MenuItemSelectedGradientBegin => EgoistTrayPalette.Hover;
        public override Color MenuItemSelectedGradientEnd => EgoistTrayPalette.Hover;
        public override Color MenuBorder => EgoistTrayPalette.Separator;
        public override Color ImageMarginGradientBegin => EgoistTrayPalette.Background;
        public override Color ImageMarginGradientMiddle => EgoistTrayPalette.Background;
        public override Color ImageMarginGradientEnd => EgoistTrayPalette.Background;
        public override Color SeparatorDark => EgoistTrayPalette.Separator;
        public override Color SeparatorLight => EgoistTrayPalette.Separator;
        public override Color CheckBackground => EgoistTrayPalette.Background;
        public override Color CheckPressedBackground => EgoistTrayPalette.Hover;
        public override Color CheckSelectedBackground => EgoistTrayPalette.Hover;
    }
}

internal static class EgoistTrayVisualPreview
{
    internal static void Render(string outputPath, EffectiveAppTheme theme = EffectiveAppTheme.Dark)
    {
        EgoistTrayPalette.Apply(theme);
        var renderer = new EgoistTrayRenderer();
        using var root = new Forms.ContextMenuStrip();
        TrayService.ConfigureDropDown(root, renderer);
        root.ShowCheckMargin = true;
        root.Items.Add(TrayService.CreateItem("Начать диктовку · пауза"));
        root.Items[^1].Enabled = false;
        root.Items.Add(TrayService.CreateItem("Возобновить микрофон"));
        ((Forms.ToolStripMenuItem)root.Items[^1]).Checked = true;
        var microphone = TrayService.CreateItem("Микрофон · Studio USB");
        TrayService.ConfigureDropDown(microphone.DropDown, renderer);
        microphone.DropDownItems.Add(TrayService.CreateItem("Системный · Studio USB"));
        root.Items.Add(microphone);
        var activation = TrayService.CreateItem("Кнопка запуска");
        TrayService.ConfigureDropDown(activation.DropDown, renderer);
        activation.DropDownItems.Add(TrayService.CreateItem("Mouse 5"));
        root.Items.Add(activation);
        var settings = TrayService.CreateItem("Настройки");
        TrayService.ConfigureDropDown(settings.DropDown, renderer);
        foreach (var label in new[]
                 {
                     "Смешанная русско-английская речь",
                     "Числа цифрами",
                     "Голосовые команды",
                     "Возвращать буфер обмена",
                     "Звуковые сигналы",
                     "Важные уведомления"
                 })
        {
            var setting = TrayService.CreateItem(label);
            setting.Checked = label is "Смешанная русско-английская речь" or
                "Звуковые сигналы" or "Важные уведомления";
            settings.DropDownItems.Add(setting);
        }
        settings.DropDownItems.Add(TrayService.CreateSeparator());
        settings.DropDownItems.Add(TrayService.CreateItem("Открыть словарь…"));
        root.Items.Add(settings);

        var history = TrayService.CreateItem("Последние записи");
        TrayService.ConfigureDropDown(history.DropDown, renderer);
        history.DropDownItems.Add(TrayService.CreateItem("19:38 · 00:07 · 81 КБ"));
        history.DropDownItems.Add(TrayService.CreateItem("19:34 · 00:11 · 126 КБ"));
        history.DropDownItems.Add(TrayService.CreateItem("19:29 · 00:04 · 53 КБ"));
        root.Items.Add(history);

        var themeItem = TrayService.CreateItem("Тема");
        TrayService.ConfigureDropDown(themeItem.DropDown, renderer);
        foreach (var label in new[] { "Системная", "Светлая", "Тёмная" })
        {
            var choice = TrayService.CreateItem(label);
            choice.Checked = label == "Системная";
            themeItem.DropDownItems.Add(choice);
        }
        root.Items.Add(themeItem);
        root.Items.Add(TrayService.CreateItem("Открыть все настройки…"));
        root.Items.Add(TrayService.CreateSeparator());
        root.Items.Add(TrayService.CreateItem("GigaAM + Whisper · готовы"));
        root.Items[^1].Enabled = false;
        root.Items.Add(TrayService.CreateSeparator());
        root.Items.Add(TrayService.CreateItem("Выход"));

        using var nested = new Forms.ContextMenuStrip();
        TrayService.ConfigureDropDown(nested, renderer);
        nested.ShowCheckMargin = true;
        nested.Items.Add(TrayService.CreateItem("Системная · следует за Windows"));
        ((Forms.ToolStripMenuItem)nested.Items[0]).Checked = true;
        nested.Items.Add(TrayService.CreateItem("Светлая"));
        nested.Items.Add(TrayService.CreateItem("Тёмная"));

        root.CreateControl();
        nested.CreateControl();
        root.PerformLayout();
        nested.PerformLayout();
        themeItem.Select();
        nested.Items[0].Select();
        var rootSize = root.GetPreferredSize(Size.Empty);
        var nestedSize = nested.GetPreferredSize(Size.Empty);
        root.Size = rootSize;
        nested.Size = nestedSize;

        using var bitmap = new Bitmap(rootSize.Width + nestedSize.Width + 24, Math.Max(rootSize.Height, nestedSize.Height) + 24);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(24, 24, 26));
        }
        root.DrawToBitmap(bitmap, new Rectangle(8, 8, rootSize.Width, rootSize.Height));
        nested.DrawToBitmap(bitmap, new Rectangle(rootSize.Width + 16, 8, nestedSize.Width, nestedSize.Height));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        bitmap.Save(outputPath, ImageFormat.Png);
    }
}
