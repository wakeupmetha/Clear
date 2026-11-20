namespace Browser;

// Перенос Sources/Search/Fuzzy.swift.
//
// Опечатки в адресном поле: слово с неверной, пропущенной или переставленной
// буквой всё равно находит своё. Только запасной вариант, ниже точных
// совпадений.
//
// Работает по байтам UTF-8: вызывается для всей истории на каждое нажатие, и
// поиск по строкам здесь в разы медленнее. Тексты уже в нижнем регистре.
public static class Fuzzy
{
    /// 1 — каждое слово есть в тексте как набрано; 0 — хотя бы одному простили
    /// опечатку; null — какого-то слова нет.
    public static int? Match(byte[][] typed, ReadOnlySpan<byte> text)
    {
        var slipped = false;
        foreach (var word in typed)
        {
            if (Contains(text, word)) continue;
            if (!Nearly(word, text)) return null;
            slipped = true;
        }
        return slipped ? 0 : 1;
    }

    public static bool Contains(ReadOnlySpan<byte> text, ReadOnlySpan<byte> word) => text.IndexOf(word) >= 0;

    /// Начинает ли word слово в тексте: «api» в «team/api», но не в «rapid».
    public static bool StartsWord(ReadOnlySpan<byte> word, ReadOnlySpan<byte> text)
    {
        if (word.Length == 0 || text.Length < word.Length) return false;
        var anywhere = !InWord(word[0]);
        var from = 0;
        while (from < text.Length)
        {
            var hit = text[from..].IndexOf(word);
            if (hit < 0) return false;
            var at = from + hit;
            if (anywhere || at == 0 || !InWord(text[at - 1])) return true;
            from = at + 1;
        }
        return false;
    }

    // Буквы, цифры и всё вне ASCII.
    static bool InWord(byte b) => b >= 0x80 || b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'z');

    /// Начинается ли какое-то слово текста с typed с точностью до одной
    /// опечатки (двух в длинных словах). Короче четырёх букв не угадываем:
    /// там догадка — это шум.
    public static bool Nearly(ReadOnlySpan<byte> typed, ReadOnlySpan<byte> text)
    {
        var allowed = typed.Length < 4 ? 0 : typed.Length < 8 ? 1 : 2;
        if (allowed == 0) return false;
        // Расстояние редактирования до начала каждого слова; перестановка двух
        // соседних букв считается за одну правку. Строки таблицы общие на все
        // слова и создаются при первом подходящем: у большинства текстов таких нет.
        var width = typed.Length + allowed + 1;
        int[]? before = null, above = null, row = null;
        var start = 0;
        while (start < text.Length)
        {
            if (!InWord(text[start])) { start++; continue; }
            var end = start;
            while (end < text.Length && InWord(text[end])) end++;
            var word = text[start..end];
            start = end;
            // Первую букву путают редко: требуем её (или её перестановку со
            // второй). Это дёшево отсекает почти все слова.
            if (word.Length < 2 || word.Length < typed.Length - allowed) continue;
            if (word[0] != typed[0] && !(word[0] == typed[1] && word[1] == typed[0])) continue;

            before ??= new int[width];
            above ??= new int[width];
            row ??= new int[width];
            var columns = Math.Min(word.Length, width - 1);
            for (var j = 0; j <= columns; j++) above[j] = j;
            var far = false;
            for (var i = 1; i <= typed.Length; i++)
            {
                row[0] = i;
                var least = i;
                for (var j = 1; j <= columns; j++)
                {
                    var best = Math.Min(Math.Min(above[j] + 1, row[j - 1] + 1),
                                        above[j - 1] + (typed[i - 1] == word[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && typed[i - 1] == word[j - 2] && typed[i - 2] == word[j - 1])
                        best = Math.Min(best, before[j - 2] + 1);
                    row[j] = best;
                    least = Math.Min(least, best);
                }
                if (least > allowed) { far = true; break; }   // опечаток уже слишком много
                (before, above, row) = (above, row, before);
            }
            if (far) continue;
            for (var j = 1; j <= columns; j++)
                if (above[j] <= allowed) return true;
        }
        return false;
    }
}
