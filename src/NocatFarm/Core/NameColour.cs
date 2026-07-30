namespace NocatFarm.Core;

/// <summary>
/// The colour an account's name is painted in the log.
/// </summary>
/// <remarks>
/// One palette, shared by the window, the console board and the dashboard, so an account is the same colour
/// wherever you happen to be looking at it. Defining it three times would guarantee they drift - which is
/// exactly what happened to the "what is this account doing" text before it was pulled into <see cref="BotStatus"/>.
///
/// Index 0 is "automatic" and means the old behaviour: colour by state rather than by account.
/// </remarks>
public static class NameColour {
	public readonly record struct Swatch(string Label, byte R, byte G, byte B) {
		/// <summary>24-bit terminal colour. Every terminal this runs in supports it; the 256-colour cube does not hold these.</summary>
		public string Ansi => $"\x1b[38;2;{R};{G};{B}m";

		/// <summary>Win32 COLORREF, which is 0x00BBGGRR - not RGB.</summary>
		public int Win32 => R | (G << 8) | (B << 16);

		public string Css => $"#{R:x2}{G:x2}{B:x2}";
	}

	/// <summary>Index matches the value stored in BotConfig.LogColour and the Choices list in Settings.cs.</summary>
	public static readonly Swatch[] All = [
		new("automatic", 0, 0, 0),
		new("cyan", 78, 201, 224),
		new("green", 106, 190, 110),
		new("amber", 224, 164, 88),
		new("pink", 232, 121, 169),
		new("purple", 170, 140, 235),
		new("blue", 96, 150, 240),
		new("red", 226, 100, 100),
		new("white", 225, 230, 238)
	];

	/// <summary>The chosen swatch, or null for "automatic" and for anything out of range.</summary>
	public static Swatch? Of(int choice) => (choice > 0) && (choice < All.Length) ? All[choice] : null;

	/// <summary>The Choices string the settings form is built from, kept in step with the array above.</summary>
	public static string ChoicesSpec =>
		string.Join(" | ", All.Select((s, i) => $"{i} {s.Label}"));
}
