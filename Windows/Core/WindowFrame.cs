using System.Globalization;

namespace Browser;

// Рамка окна между запусками (window.frame): где окно стояло и какого было
// размера, в точках экрана. В апстриме главное окно восстанавливает macOS сам,
// своего ключа нет; у его маленьких окон — float.frame и little.frame.
public static class WindowFrame
{
    public readonly record struct Box(int X, int Y, int Width, int Height);

    /// На столько ниже верхнего края берётся точка, за которую окно таскают:
    /// окно, задвинутое краем чуть выше экрана, ещё на нём.
    const int Grip = 16;

    public static string Write(Box box) => $"{box.X},{box.Y},{box.Width},{box.Height}";

    /// Испорченная строка — как будто рамки нет: окно встанет, как в первый раз.
    public static Box? Read(string? text)
    {
        var parts = text?.Split(',');
        if (parts is not { Length: 4 }) return null;
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return numbers[2] > 0 && numbers[3] > 0 ? new Box(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }

    /// Куда поставить окно с прошлой рамкой при нынешних экранах (их рабочих
    /// областях, без панели задач). Экран — тот, на котором больше всего полосы
    /// окна (а не её середина: у окна шире экрана она за краем); полосы нет ни
    /// на одном (экран отключили) — null, и окно встаёт как в первый раз. На
    /// своём экране окно ужимается до его размера и сдвигается внутрь целиком.
    // ponytail: точки экрана, а не независимые от плотности: окно, закрытое на
    // экране с другим масштабом, откроется на нём того же размера в точках.
    public static Box? Fit(Box saved, IEnumerable<Box> screens)
    {
        var y = saved.Y + Grip;
        Box? best = null;
        var most = 0;
        foreach (var screen in screens)
        {
            if (y < screen.Y || y >= screen.Y + screen.Height) continue;
            var overlap = Math.Min(saved.X + saved.Width, screen.X + screen.Width) - Math.Max(saved.X, screen.X);
            if (overlap <= most) continue;
            most = overlap;
            best = screen;
        }
        if (best is not { } on) return null;
        var width = Math.Min(saved.Width, on.Width);
        var height = Math.Min(saved.Height, on.Height);
        return new Box(
            Math.Clamp(saved.X, on.X, on.X + on.Width - width),
            Math.Clamp(saved.Y, on.Y, on.Y + on.Height - height),
            width, height);
    }
}
