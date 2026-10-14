using System.Globalization;

namespace NocatFarm;

// How a few words read the same wherever a command is typed: 'all' where no account means every account anyway, and
// what Steam chat leaves alone or fills in.
public static partial class Commands {
	/// <summary>
	/// The arguments of a command whose bare form already means every account ('human', 'habits', 'cards', '2fa',
	/// 'confirmations', 'selfcheck'), with an 'all' in front taken off - 'human all' said "there's no account called 'all'".
	/// </summary>
	internal static string[] AllIsEvery(string[] args) =>
		(args.Length > 0) && args[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? args[1..] : args;

	/// <summary>
	/// The words a command that defaults to this account takes where an account would go ('pause 30', 'human week',
	/// 'points spend preview', 'value refresh'). Any other word there is an account name mistyped, and from Steam chat it
	/// is left for the command to say so - filled in with this account, 'stop kylr0' stopped this one and '2fa kylr0' gave
	/// this one's code.
	/// </summary>
	private static readonly HashSet<string> FillsAround = new(StringComparer.OrdinalIgnoreCase) {
		"all", "week", "reroll", "forget", "open", "spend", "preview", "on", "off", "do", "now", "ok", "refresh", "list", "plan"
	};

	/// <summary>Whether this account's name goes in front of <paramref name="word"/> from Steam chat (see <see cref="FillsAround"/>).</summary>
	private static bool FillsInFront(string word) =>
		FillsAround.Contains(word) || double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
		|| System.Text.RegularExpressions.Regex.IsMatch(word, @"^\d+(\.\d+)?[smhd]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)   // 'pause 30m', 'pause 2h'
		|| word.Split(',', StringSplitOptions.RemoveEmptyEntries).All(static w => uint.TryParse(w, out _));

	/// <summary>
	/// Whether an 'all' typed where a command takes an account means every account: not for a command that takes no
	/// account ('help all', 'clear all'), nor 'config all' - there it's the advanced settings, of this account from its chat.
	/// A plugin's command is taken to mean every account, to be safe.
	/// </summary>
	private static bool AllIsAccounts(string command) =>
		Resolve(command) is not { } def || ((def.Name != "config") && def.Args.Contains("account", StringComparison.Ordinal));

	/// <summary>
	/// What spends or sells from Steam chat, like 'points spend' and 'spare do': 'sell do' and 'sell relist' put items on the
	/// market, and 'booster &lt;appIDs&gt;' turns gems into packs. Looking ('sell preview', 'booster') is fine.
	/// </summary>
	private static bool SpendsFromChat(string[] words) {
		if (words.Length < 2) {
			return false;
		}

		string? name = Resolve(words[0])?.Name;
		string[] rest = [.. words[1..].Select(static w => w.ToLowerInvariant())];

		return ((name == "sell") && rest.Any(static w => w is "do" or "relist"))
			|| ((name == "booster") && !rest.Contains("open") && rest.Any(static w => (w.Length > 0) && char.IsAsciiDigit(w[0])));
	}
}
