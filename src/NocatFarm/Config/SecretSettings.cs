using System.Reflection;
using NocatFarm.Core;

namespace NocatFarm.Config;

/// <summary>
/// Every global secret setting kept encrypted on disk - by its kind (<see cref="SettingKind.Secret"/>), not by name, so
/// one added later (the backup password) is encrypted without a line of its own in the load and save. The six that
/// have always had their own lines keep them; this does the rest.
/// </summary>
internal static class SecretSettings {
	private static readonly HashSet<string> OwnLines = new(StringComparer.Ordinal) {
		nameof(GlobalConfig.DiscordWebhookUrl), nameof(GlobalConfig.TelegramBotToken), nameof(GlobalConfig.DiscordBotToken),
		nameof(GlobalConfig.WebPassword), nameof(GlobalConfig.Rep4RepApiToken), nameof(GlobalConfig.WebProxyPassword)
	};

	private static IEnumerable<PropertyInfo> Others() =>
		Settings.Global.Where(static d => (d.Kind == SettingKind.Secret) && !OwnLines.Contains(d.Name))
			.Select(static d => typeof(GlobalConfig).GetProperty(d.Name))
			.Where(static p => (p != null) && (p.PropertyType == typeof(string)) && p.CanWrite)!;

	/// <summary>Read from disk: each opened. True when any was still plain text there, so it's written back encrypted.</summary>
	internal static bool Open(GlobalConfig loaded) {
		bool plain = false;

		foreach (PropertyInfo p in Others()) {
			string stored = p.GetValue(loaded) as string ?? "";
			plain |= Secrets.IsPlain(stored);
			p.SetValue(loaded, Secrets.Unprotect(stored));
		}

		return plain;
	}

	/// <summary>The copy going to disk: each encrypted from the live value.</summary>
	internal static void Seal(GlobalConfig onDisk, GlobalConfig live) {
		foreach (PropertyInfo p in Others()) {
			p.SetValue(onDisk, Secrets.Protect(p.GetValue(live) as string ?? "", "global"));
		}
	}
}
