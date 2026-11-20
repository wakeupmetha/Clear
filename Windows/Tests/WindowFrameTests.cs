namespace Browser.Tests;

static class WindowFrameTests
{
    public static void Run()
    {
        var box = new WindowFrame.Box(-1500, 20, 1280, 840);
        Check.Expect(WindowFrame.Read(WindowFrame.Write(box)) == box, "рамка пишется и читается обратно, с минусом тоже");
        Check.Expect(((string?[])[null, "", "1,2,3", "1,2,3,x", "1,2,0,5", "1,2,3,4,5"]).All(text => WindowFrame.Read(text) is null),
            "испорченная рамка и нулевой размер читаются как отсутствующие");

        WindowFrame.Box[] screens = [new(0, 0, 1920, 1040), new(-1280, 100, 1280, 984)];
        Check.Expect(WindowFrame.Fit(new(100, 50, 1280, 840), screens) == new WindowFrame.Box(100, 50, 1280, 840), "на экране — как была");
        Check.Expect(WindowFrame.Fit(new(-1200, 150, 1000, 700), screens) == new WindowFrame.Box(-1200, 150, 1000, 700), "на втором экране слева — там же");
        Check.Expect(WindowFrame.Fit(new(1000, 600, 1280, 840), screens) == new WindowFrame.Box(640, 200, 1280, 840),
            "вылезла за правый и нижний край — сдвинута внутрь целиком");
        Check.Expect(WindowFrame.Fit(new(100, -8, 3000, 2000), screens) == new WindowFrame.Box(0, 0, 1920, 1040),
            "больше экрана и краем выше него — ужата до экрана");
        Check.Expect(WindowFrame.Fit(new(3000, 100, 1280, 840), screens) is null && WindowFrame.Fit(new(100, 1100, 1280, 840), screens) is null,
            "на отключённом экране — null: окно встанет как в первый раз");
        Check.Expect(WindowFrame.Fit(new(-640, 50, 1280, 840), screens) == new WindowFrame.Box(0, 50, 1280, 840),
            "на стыке поровну — первый экран");
        Check.Expect(WindowFrame.Fit(new(100, 100, 9000, 9000), screens) == new WindowFrame.Box(0, 0, 1920, 1040),
            "шире экрана (середина полосы за краем) — ужата до экрана, а не сброшена");
        Check.Expect(WindowFrame.Fit(new(-1100, 150, 2000, 600), screens) == new WindowFrame.Box(-1280, 150, 1280, 600),
            "больше полосы на левом (второй в списке) — на левом, ужата до его ширины");
    }
}
