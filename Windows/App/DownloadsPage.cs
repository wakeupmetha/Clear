using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Browser;

/// Загрузки, пока они идут (FetchEntry и Fetches из Fetching.swift): саму
/// загрузку ведёт движок, здесь — список для страницы загрузок и кнопки в
/// полосе. Законченные уходят в Downloads (downloads.json).
static class Fetching
{
    public sealed class Entry(CoreWebView2DownloadOperation operation, string from)
    {
        public readonly CoreWebView2DownloadOperation Operation = operation;
        public readonly string From = from;
        public string Name => Path.GetFileName(Operation.ResultFilePath);
        public bool Paused => Operation.State == CoreWebView2DownloadState.Interrupted
            && Operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused;
        public bool Failed => Operation.State == CoreWebView2DownloadState.Interrupted && !Paused;

        /// Строка под именем: сколько пришло из скольких.
        public string Progress
        {
            get
            {
                var got = Downloads.Size((long)Operation.BytesReceived);
                var of = Operation.TotalBytesToReceive is > 0 and var total ? $"{got} of {Downloads.Size((long)total)}" : got;
                return Paused ? "Paused · " + of : Failed ? "Failed" : of;
            }
        }
    }

    public static readonly List<Entry> Active = [];
    /// Загрузка появилась, ушла или сменила состояние (не на каждый байт).
    public static event Action? Changed;

    public static void Watch(CoreWebView2DownloadOperation operation, Tab tab)
    {
        // Адрес файла и из приватной вкладки не пишется (Keep.address апстрима).
        var address = tab.Private ? null : Downloads.Address(operation.Uri);
        var site = Uri.TryCreate(operation.Uri, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : tab.Page;
        var from = site is null ? "" : Address.WithoutWWW(site.Host);
        var entry = new Entry(operation, from);
        Active.Add(entry);
        operation.StateChanged += (_, _) =>
        {
            if (operation.State == CoreWebView2DownloadState.Completed)
            {
                Active.Remove(entry);
                Downloads.Add(new Keep { Name = entry.Name, From = from, Path = operation.ResultFilePath, Date = DateTime.UtcNow, Address = address });
            }
            else if (operation.State == CoreWebView2DownloadState.Interrupted
                && operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled) Active.Remove(entry);
            Changed?.Invoke();
        };
        Changed?.Invoke();
    }

    /// Неудавшуюся, которую не продолжить, человек убирает из списка сам.
    public static void Drop(Entry entry)
    {
        Active.Remove(entry);
        Changed?.Invoke();
    }
}

/// Страница загрузок (DownloadsPanel из Recall.swift; в апстриме по умолчанию
/// она во вкладке): идущие сейчас и законченные.
// ponytail: список собирается заново на каждую смену (кнопка, на которой
// стоял фокус, пропадает вместе с ним). Строк здесь не больше полусотни;
// мешает — менять только изменившуюся строку.
sealed partial class DownloadsPage : Grid
{
    readonly StackPanel column = new() { MaxWidth = 600, Padding = new Thickness(24, 26, 24, 24), Spacing = 22 };
    /// Строки идущих загрузок: их цифры обновляются по часам, а не на каждый байт.
    readonly List<(Fetching.Entry Entry, TextBlock Text)> moving = [];
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public DownloadsPage()
    {
        Background = Palette.Ground;
        var scroll = new ScrollViewer
        {
            Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
        };
        // Колонке с MaxWidth в прокрутке ширину задаём сами, иначе она не по середине.
        scroll.SizeChanged += (_, _) => column.Width = Math.Min(600, scroll.ActualWidth);
        Children.Add(scroll);
        tick.Tick += (_, _) => { foreach (var (entry, text) in moving) text.Text = entry.Progress; };
        // Слушаем, только пока страница во вкладке: закрытую не держим в памяти.
        Loaded += (_, _) =>
        {
            Fetching.Changed += Fill;
            Downloads.Changed += Fill;
            Fill();
            tick.Start();
        };
        Unloaded += (_, _) =>
        {
            Fetching.Changed -= Fill;
            Downloads.Changed -= Fill;
            tick.Stop();
        };
        // Файл могли стереть, пока страница стояла за другой вкладкой.
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (IsLoaded && Visibility == Visibility.Visible) Fill(); });
    }

    void Fill()
    {
        column.Children.Clear();
        moving.Clear();
        column.Children.Add(Ui.Heading(Ui.Download, "Downloads"));
        if (Fetching.Active.Count > 0)
            column.Children.Add(SettingsPage.Group("Current downloads", Ui.Download, [.. Fetching.Active.Select(Moving)]));
        if (Downloads.Kept.Count > 0)
            column.Children.Add(SettingsPage.Group("Completed", Ui.Check, [.. Downloads.Kept.Select(Kept)]));

        var foot = new Grid { ColumnSpacing = 12 };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(new TextBlock
        {
            FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Text = Fetching.Active.Count == 0 && Downloads.Kept.Count == 0 ? "Nothing downloaded yet. Files land in your Downloads folder"
                : Downloads.Kept.Count == 0 ? "Files land in your Downloads folder" : "Clearing the list leaves the files where they are",
        });
        if (Downloads.Kept.Count > 0)
        {
            var clear = Ui.Pill("Clear list", Ui.Word("Clear List"), Downloads.ForgetAll);
            SetColumn(clear, 1);
            foot.Children.Add(clear);
        }
        column.Children.Add(foot);
    }

    StackPanel Moving(Fetching.Entry entry)
    {
        var progress = new TextBlock { Text = entry.Progress, FontSize = 11.5, Foreground = Palette.Muted };
        moving.Add((entry, progress));
        var operation = entry.Operation;
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Paused || (entry.Failed && operation.CanResume))
            acts.Children.Add(Ui.Pill($"Resume {entry.Name}", Ui.Word("Resume"), operation.Resume));
        else if (!entry.Failed)
            acts.Children.Add(Ui.Pill($"Pause {entry.Name}", Ui.Word("Pause"), operation.Pause));
        acts.Children.Add(entry.Failed && !operation.CanResume
            ? Ui.Pill($"Remove {entry.Name}", Ui.Word("Remove"), () => Fetching.Drop(entry))
            : Ui.Pill($"Cancel {entry.Name}", Ui.Word("Cancel"), operation.Cancel));
        return SettingsPage.Row(Ui.Download, entry.Name, entry.From.Length > 0 ? entry.From : "Downloading", acts, progress);
    }

    static StackPanel Kept(Keep keep)
    {
        // Файл могли стереть или унести: строка остаётся, открыть уже нечего.
        var there = File.Exists(keep.Path);
        var when = keep.Date.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);
        var detail = string.Join(" · ", new[] { keep.From, there ? when : "Deleted" }.Where(part => part.Length > 0));
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (there)
        {
            acts.Children.Add(Ui.Pill($"Open {keep.Name}", Ui.Word("Open"), () => Launch(keep.Path)));
            acts.Children.Add(Ui.Pill($"Show {keep.Name} in folder", Ui.Word("Show in Folder"),
                () => Launch("explorer.exe", $"/select,\"{keep.Path}\"")));
        }
        acts.Children.Add(Ui.Pill($"Forget {keep.Name}", Ui.Glyph(Ui.Cross, 11, Palette.Muted), () => Downloads.Forget(keep.Path)));
        return SettingsPage.Row(Ui.File, keep.Name, detail, acts);
    }

    /// Открыть файл той программой, что назначена в Windows, или Проводник.
    static void Launch(string file, string? arguments = null)
    {
        try { Process.Start(new ProcessStartInfo(file, arguments ?? "") { UseShellExecute = true }); }
        // Файла уже нет или ему не назначена программа: Windows сказала бы сама,
        // но из UseShellExecute приходит исключение — окно от этого не падает.
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { }
    }
}
