using System.Numerics;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Packs;

/// <summary>Мод в коде набора: номер мода на модбазе и порядковый номер его выпуска (от самого старого).</summary>
public sealed record ModCodeEntry(long ModDbId, int ReleaseIndex);

/// <summary>Разобранный код: моды и версия игры, на которой собран набор (если её знали).</summary>
public sealed record ModCodeSet(IReadOnlyList<ModCodeEntry> Entries, ModVersion? GameVersion);

/// <summary>
/// Код набора модов: строка вида «EV1-…», которую можно скинуть в чат, — без сервера, всё внутри строки.
/// В коде на каждый мод — номер мода на модбазе и порядковый номер выпуска (выпуски мода по возрастанию номера
/// выпуска; новые встают в конец, поэтому номер не меняется). Номера модов сортируются и пишутся разностями,
/// разности и номера выпусков — кодом Райса (k подбирается под набор), в конце — CRC-16, чтобы испорченный при
/// копировании код не поставил не то. Ещё в коде версия игры набора (2–3 символа), чтобы предупредить о разнице. Алфавит — base58 (без 0/O и I/l): код можно и продиктовать.
/// В среднем около 3 символов на мод.
/// </summary>
public static class ModCode
{
    public const string Prefix = "EV1-";
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>Код набора; моды с одним номером на модбазе — один раз (первая запись).</summary>
    public static string Encode(IEnumerable<ModCodeEntry> entries, ModVersion? gameVersion = null)
    {
        var list = entries.GroupBy(e => e.ModDbId).Select(g => g.First()).OrderBy(e => e.ModDbId).ToList();
        if (list.Count == 0) throw new ArgumentException("empty set", nameof(entries));
        if (list.Any(e => e.ModDbId < 0 || e.ReleaseIndex < 0)) throw new ArgumentOutOfRangeException(nameof(entries));

        var bits = new BitWriter();
        bits.Gamma((ulong)list.Count);
        bits.Bit(gameVersion is not null);
        if (gameVersion is not null)
            foreach (var part in new[] { gameVersion.Major, gameVersion.Minor, gameVersion.Patch }) bits.Gamma((ulong)Math.Max(0, part) + 1);
        var deltas = list.Select((e, i) => (ulong)(e.ModDbId - (i == 0 ? 0 : list[i - 1].ModDbId))).ToList();
        bits.RiceList(deltas);
        bits.RiceList(list.Select(e => (ulong)e.ReleaseIndex).ToList());
        var payload = bits.ToBytes();
        var crc = Crc16(payload);
        return Prefix + ToBase58([.. payload, (byte)(crc >> 8), (byte)crc]);
    }

    /// <summary>Разобрать код (пробелы, переносы и кавычки чата вокруг не мешают). Не код или испорчен — FormatException.</summary>
    public static ModCodeSet Decode(string text)
    {
        // неразрывный и прочие дефисы — как «-»: так код показывает окно, и так его меняют некоторые чаты
        var code = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '`' && c != '"' && c != '\'')
            .Select(c => c is '‐' or '‑' or '‒' or '–' or '−' ? '-' : c).ToArray());
        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException(code.StartsWith("EV", StringComparison.OrdinalIgnoreCase) && code.Length > 3 && code[3] == '-'
                ? Loc.T("modcode.newer") : Loc.T("modcode.notACode"));
        var bytes = FromBase58(code[Prefix.Length..]) ?? throw new FormatException(Loc.T("modcode.damaged"));
        if (bytes.Length < 3) throw new FormatException(Loc.T("modcode.damaged"));
        var payload = bytes[..^2];
        if (Crc16(payload) != (ushort)(bytes[^2] << 8 | bytes[^1])) throw new FormatException(Loc.T("modcode.damaged"));

        try
        {
            var bits = new BitReader(payload);
            var count = bits.Gamma();
            if (count is 0 or > 5000) throw new FormatException(Loc.T("modcode.damaged"));
            ModVersion? game = null;
            if (bits.Bit())
            {
                var parts = new[] { bits.Gamma() - 1, bits.Gamma() - 1, bits.Gamma() - 1 };
                game = ModVersion.ParseOrNull(string.Join('.', parts));
            }
            var deltas = bits.RiceList((int)count);
            var indexes = bits.RiceList((int)count);
            var result = new List<ModCodeEntry>((int)count);
            long id = 0;
            for (var i = 0; i < (int)count; i++)
            {
                id += (long)deltas[i];
                result.Add(new ModCodeEntry(id, checked((int)indexes[i])));
            }
            return new ModCodeSet(result, game);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or OverflowException)
        {
            throw new FormatException(Loc.T("modcode.damaged"));
        }
    }

    /// <summary>Выпуски мода в порядке кода: по возрастанию номера выпуска (новые — в конце).</summary>
    public static List<ModDbRelease> OrderedReleases(ModDbMod mod) => [.. mod.Releases.OrderBy(r => r.ReleaseId)];

    // ---- base58 (как у адресов Bitcoin): ведущий байт 1 — чтобы нули в начале данных не потерялись
    private static string ToBase58(byte[] data)
    {
        var n = new BigInteger([1, .. data], isUnsigned: true, isBigEndian: true);
        var chars = new List<char>();
        while (n > 0)
        {
            n = BigInteger.DivRem(n, 58, out var r);
            chars.Add(Alphabet[(int)r]);
        }
        chars.Reverse();
        return new string([.. chars]);
    }

    private static byte[]? FromBase58(string text)
    {
        BigInteger n = 0;
        foreach (var c in text)
        {
            var i = Alphabet.IndexOf(c);
            if (i < 0) return null;
            n = n * 58 + i;
        }
        var bytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        return bytes.Length > 0 && bytes[0] == 1 ? bytes[1..] : null;
    }

    // CRC-16/CCITT-FALSE
    private static ushort Crc16(byte[] data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public void Bit(bool b) => _bits.Add(b);

        public void Bits(ulong value, int count)
        {
            for (var i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }

        /// <summary>Гамма-код Элиаса (n ≥ 1): длина нулями, затем само число.</summary>
        public void Gamma(ulong n)
        {
            var len = 64 - BitOperations.LeadingZeroCount(n);
            for (var i = 1; i < len; i++) _bits.Add(false);
            Bits(n, len);
        }

        /// <summary>Список кодом Райса с лучшим для него k (4 бита в начале).</summary>
        public void RiceList(IReadOnlyList<ulong> values)
        {
            var k = Enumerable.Range(0, 16).MinBy(k => values.Sum(v => (double)(v >> k) + 1 + k));
            Bits((ulong)k, 4);
            foreach (var v in values)
            {
                for (ulong q = v >> k; q > 0; q--) _bits.Add(true);
                _bits.Add(false);
                Bits(v & ((1UL << k) - 1), k);
            }
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            return bytes;
        }
    }

    private sealed class BitReader(byte[] data)
    {
        private int _pos;

        public bool Bit()
        {
            if (_pos >= data.Length * 8) throw new IndexOutOfRangeException();
            var b = (data[_pos / 8] & (0x80 >> (_pos % 8))) != 0;
            _pos++;
            return b;
        }

        public ulong Bits(int count)
        {
            ulong v = 0;
            for (var i = 0; i < count; i++) v = v << 1 | (Bit() ? 1UL : 0);
            return v;
        }

        public ulong Gamma()
        {
            var zeros = 0;
            while (!Bit()) if (++zeros > 32) throw new OverflowException();
            return (1UL << zeros) | Bits(zeros);
        }

        public List<ulong> RiceList(int count)
        {
            var k = (int)Bits(4);
            var list = new List<ulong>(count);
            for (var i = 0; i < count; i++)
            {
                ulong q = 0;
                while (Bit()) if (++q > 1_000_000) throw new OverflowException();
                list.Add(q << k | Bits(k));
            }
            return list;
        }
    }
}
