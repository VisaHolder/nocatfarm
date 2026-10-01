using System.Globalization;
using System.Text.Json;

using NocatFarm.Core;

using static NocatFarm.Config.ImportFiles;

namespace NocatFarm.Config;

/// <summary>
/// SingleBoostr works through whichever account the Steam app is signed into, so there's no account to bring - only
/// its card-farming choices: the games it never farms, and how long a game had to be played before it farmed it.
/// </summary>
public sealed class SingleBoostrImport : IIdlerImporter {
	public string Id => "singleboostr";
	public string Name => "SingleBoostr";

	public IEnumerable<string> Places() => SearchUsual(d => Resolve(d) != null);

	public string? Resolve(string path) {
		string file = Directory.Exists(path) ? Path.Combine(path, "Settings.json") : path;
		JsonElement root = Json(file);

		// Not HourBoostr's file of the same name: no Accounts list, and SingleBoostr's own keys.
		return (root.ValueKind == JsonValueKind.Object) && (Prop(root, "Accounts").ValueKind == JsonValueKind.Undefined)
			&& ((Prop(root, "BlacklistedCardGames").ValueKind != JsonValueKind.Undefined) || (Prop(root, "WebSession").ValueKind != JsonValueKind.Undefined))
				? file
				: null;
	}

	public ImportScan Read(string resolved) {
		ImportScan scan = new() { Tool = Id, ToolName = Name, Path = resolved };
		JsonElement root = Json(resolved);

		if (Apps(Prop(root, "BlacklistedCardGames")) is { Count: > 0 } blacklist) {
			scan.Settings.Add(new ImportSetting("GlobalBlacklistedGames", string.Join(", ", blacklist), ToGlobal: g => {
				// A new list, not added to in place: this is the live one, and a save or a copy reading it at the same moment
				// threw "collection was modified" half way through.
				g.GlobalBlacklistedGames = [.. g.GlobalBlacklistedGames, .. blacklist.Where(a => !g.GlobalBlacklistedGames.Contains(a))];
			}));
		}

		// Minutes there, hours here. Only when the switch beside it was on (older files have no switch).
		if ((Bool(root, "OnlyIdleGamesWithCertainMinutes") != false) && (Num(root, "NumOnlyIdleGamesWithCertainMinutes") is double minutes) && (minutes > 0)) {
			float hours = (float) Math.Min(100, Math.Round(minutes / 60.0, 1));
			scan.Settings.Add(new ImportSetting("HoursUntilCardDrops", hours.ToString("0.#", CultureInfo.InvariantCulture), ToAccounts: b => b.HoursUntilCardDrops = hours));
		}

		scan.Notes.Add(new Said("SingleBoostr uses the account your Steam app is signed into, so there's no account to bring - add it here yourself"));

		return scan;
	}
}
