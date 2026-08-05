namespace NocatFarm.Core;

/// <summary>
/// Something a module has said about itself, kept as the English it was written as and translated on read.
/// </summary>
/// <remarks>
/// A module writes its status once and then leaves it alone - the card farmer says "nothing left to farm" when
/// it finishes and does not touch it again for hours. Storing the TRANSLATED text meant that sentence was
/// frozen in whatever language was selected the moment it was written: change the language and the account
/// cards kept their old wording indefinitely, because nothing was ever going to rewrite them. Half the readout
/// would switch and half would not, which reads as the setting being broken rather than as a cache.
///
/// Holding the English and the values, and translating in the getter, makes a language change take effect on
/// the very next read with no module needing to know it happened. It also gives the log a way to ask for the
/// English - a log is a record, and it should say the same thing whoever is reading it back.
///
/// A struct because a module holds exactly one of these and swaps it whole; there is nothing to share.
/// </remarks>
public readonly struct Said(string english, params object?[] args) {
	/// <summary>The sentence as it was written in the source - the translation key, and what the log uses.</summary>
	/// <remarks>
	/// Null on a default(Said), and everything here has to survive that. A struct's default skips the
	/// constructor entirely, so the `?? ""` above never runs for one - and `default` is exactly what an
	/// absent status is written as ("no warning", "no persona to mention"). Reading English.Length on one of
	/// those threw, and it threw in the account readout, which is to say the moment anybody opened Accounts.
	/// </remarks>
	public string English { get; } = english ?? "";

	private object?[] Args { get; } = args ?? [];

	/// <summary>True when nothing has been said yet - including on a default(Said).</summary>
	public bool IsEmpty => string.IsNullOrEmpty(English);

	/// <summary>The sentence in whatever language is selected right now.</summary>
	public override string ToString() => IsEmpty ? "" : Loc.T(English, Args ?? []);

	/// <summary>So a status can be handed to anything expecting a plain string without ceremony.</summary>
	public static implicit operator string(Said said) => said.ToString();
}
