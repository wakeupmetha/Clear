using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Browser;

/// Значок сайта, а пока его нет — буква на бледной плитке; у своей страницы —
/// её значок. Mark из Sources/Search/Icons.swift. Значка, которого нет на диске, знак просит сам
/// (Icons.Ensure): сайт из истории получает свой, даже если его не открывали.
// Grid, а не Border: Border в WinUI запечатан.
sealed class Mark : Grid
{
    /// Картинки по сайту: файл читается один раз, а не на каждую строку
    /// истории. null — значка нет.
    static readonly Dictionary<string, ImageSource?> Seen = [];
    /// Сайты, за чьим значком уже ходили в эту сессию.
    static readonly HashSet<string> Wanted = [];
    /// Файлы, которые не разобрались: к ним не возвращаемся до перезапуска.
    static readonly HashSet<string> Broken = [];
    /// Значок сайта появился или сменился; null — значков больше нет.
    static event Action<string?>? Arrived;

    readonly TextBlock letter;
    readonly Icon sign;
    ImageSource? worn;
    Uri? url;

    public Mark(double size)
    {
        Width = Height = size;
        CornerRadius = new CornerRadius(size * 0.22);
        VerticalAlignment = VerticalAlignment.Center;
        letter = new TextBlock
        {
            FontSize = size * 0.56, FontWeight = FontWeights.Medium, Foreground = Palette.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(letter);
        sign = Ui.Glyph(Ui.Gear, size, Palette.InkSoft);
        sign.Visibility = Visibility.Collapsed;
        Children.Add(sign);
        Background = Palette.InkTile;
        // Подписка живёт, пока знак в окне: строки подсказок пересоздаются на
        // каждую набранную букву и не должны копиться в событии.
        Loaded += (_, _) => Arrived += Repaint;
        Unloaded += (_, _) => Arrived -= Repaint;
    }

    /// Знак этого адреса; monogram — пока значка нет.
    public void Show(Uri? url, string monogram)
    {
        this.url = url;
        letter.Text = monogram;
        // Своя страница — её значок без плитки, как в апстриме (InternalPage.glyph):
        // буква «S» у настроек говорила бы о сайте, которого нет.
        if (url is not null && Sign(url) is { } outline)
        {
            worn = null;
            letter.Visibility = Visibility.Collapsed;
            Ui.Draw(sign, outline);
            sign.Visibility = Visibility.Visible;
            Background = Palette.Clear;
            return;
        }
        sign.Visibility = Visibility.Collapsed;
        if (url is not null && Picture(url) is { } picture)
        {
            // Строка вкладки перерисовывается на каждую смену заголовка:
            // тот же значок не надевается заново.
            if (worn == picture) return;
            worn = picture;
            letter.Visibility = Visibility.Collapsed;
            // Фоном, а не картинкой внутри: так значок скругляется вместе с плиткой.
            Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill };
            return;
        }
        worn = null;
        letter.Visibility = Visibility.Visible;
        Background = Palette.InkTile;
        if (url is not null) Want(url);
    }

    /// Значок сайта записан заново (его отдал движок): знаки сайта надевают его.
    public static void Refresh(string site)
    {
        Seen.Remove(site);
        Broken.Remove(site);
        Arrived?.Invoke(site);
    }

    /// Clear History: значки тоже говорят, где человек был. Wanted не чистим:
    /// иначе знаки открытых вкладок тут же снова пошли бы в сеть за стёртым.
    // ponytail: сайт, за которым уже ходили, до перезапуска получает значок
    // только от движка. Мешает — чистить Wanted через минуту после Clear.
    public static void ForgetAll()
    {
        Icons.ForgetAll();
        Seen.Clear();
        Broken.Clear();
        Arrived?.Invoke(null);
    }

    void Repaint(string? site)
    {
        if (url is null || (site is not null && Icons.Site(url) != site)) return;
        worn = null;
        Show(url, letter.Text);
    }

    /// Значок своей страницы браузера и страницы флагов движка (её открывают из
    /// настроек, «Engine experiments»); null — это сайт.
    static string? Sign(Uri url) =>
        Own.Is(url) ? (url.Host switch { "history" => Ui.Clock, "downloads" => Ui.Download, "bookmarks" => Ui.Bookmark, _ => Ui.Gear })
        : url.Scheme == "edge" && url.Host == "flags" ? Ui.Flask : null;

    static ImageSource? Picture(Uri url)
    {
        if (Icons.Site(url) is not { } site || Broken.Contains(site)) return null;
        if (Seen.TryGetValue(site, out var known)) return known;
        BitmapImage? picture = null;
        if (Icons.Find(url) is { } file)
        {
            // IgnoreImageCache: новый значок сайта ложится в файл с тем же именем.
            picture = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache, DecodePixelWidth = 64, UriSource = new Uri(file),
            };
            picture.ImageFailed += (_, _) =>
            {
                Broken.Add(site);
                Seen.Remove(site);
                Arrived?.Invoke(site);
            };
        }
        return Seen[site] = picture;
    }

    static async void Want(Uri url)
    {
        if (Icons.Site(url) is not { } site || Broken.Contains(site) || !Wanted.Add(site)) return;
        // Ensure не бросает: любая беда с сетью — просто «значка нет».
        if (await Icons.Ensure(url)) Refresh(site);
    }
}
