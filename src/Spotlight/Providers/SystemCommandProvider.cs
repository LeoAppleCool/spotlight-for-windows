using Microsoft.Win32;
using Spotlight.Core;
using Spotlight.Native;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>Lock, sleep, shut down, restart, sign out, empty recycle bin. Destructive ones need a second Enter.</summary>
internal sealed class SystemCommandProvider : ISearchProvider
{
    private sealed record Command(string Id, string Glyph, bool Confirm, Action Run, MatchTarget[] Targets);

    private readonly List<Command> _commands = [];

    public SystemCommandProvider()
    {
        Add("Lock", Glyphs.Lock, false, () => NativeMethods.LockWorkStation());
        Add("Sleep", Glyphs.Sleep, false, () => NativeMethods.SetSuspendState(false, false, false));
        if (HibernationEnabled())
            Add("Hibernate", Glyphs.Hibernate, false, () => NativeMethods.SetSuspendState(true, false, false));
        Add("Shutdown", Glyphs.Power, true, () => Launcher.Open("shutdown.exe", "/s /t 0"));
        Add("Restart", Glyphs.Restart, true, () => Launcher.Open("shutdown.exe", "/r /t 0"));
        Add("SignOut", Glyphs.SignOut, true, () => NativeMethods.ExitWindowsEx(NativeMethods.EWX_LOGOFF, 0));
        Add("EmptyRecycleBin", Glyphs.Delete, true, () => NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, NativeMethods.SHERB_NOCONFIRMATION));
    }

    private void Add(string id, string glyph, bool confirm, Action run)
    {
        var names = new[] { Loc.Get("Cmd." + id) }.Concat(Loc.Keywords("Cmd." + id + ".Keywords"));
        _commands.Add(new Command(id, glyph, confirm, run, names.Select(n => new MatchTarget(n)).ToArray()));
    }

    private static bool HibernationEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("HibernateEnabled") is int v && v == 1;
        }
        catch { return false; }
    }

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        if (query.Norm.Length < 2) return;
        foreach (var c in _commands)
        {
            int score = 0;
            for (int i = 0; i < c.Targets.Length; i++)
            {
                int s = Matcher.Score(query, c.Targets[i], allowFuzzy: false);
                score = Math.Max(score, i == 0 ? s : s * 9 / 10);
            }
            if (score < 700) continue; // only clear matches; never suggest "shut down" by accident

            results.Add(new SearchResult
            {
                Key = "cmd:" + c.Id,
                Title = Loc.Get("Cmd." + c.Id),
                Subtitle = Loc.Get("Sub.SystemCommand"),
                Category = Category.System,
                Score = score,
                Glyph = c.Glyph,
                GlyphFont = Glyphs.Font,
                NeedsConfirmation = c.Confirm,
                Execute = c.Run,
                Launch = new LaunchSpec("system", c.Id),
            });
        }
    }

    /// <summary>Runs a command by id (used when it is picked from the history).</summary>
    public void Run(string id) => _commands.FirstOrDefault(c => c.Id == id)?.Run();
}
