namespace NocatFarm.Core;

/// <summary>
/// What one spare-items look may still do on a human-mode account, after its items: packing gems into sacks, sending or
/// listing a sack, and taking down an unsold listing each count against the same "items a look" number, and each waits
/// the human-mode gap after whatever came before it - nobody packs gems, sells a sack and pulls three listings within
/// the same second. On a robot account there's no budget: those keep their own short gaps.
/// </summary>
/// <param name="left">Steam actions this look may still take.</param>
/// <param name="acted">Something was already done in this look - the next one waits first.</param>
public sealed class LookBudget(int left, bool acted) {
	public int Left { get; private set; } = Math.Max(0, left);

	public bool Acted { get; private set; } = acted;

	/// <summary>
	/// One more action: false when the look's number is used up, or the wait was cut short (closing). Otherwise it waits
	/// the gap first when something came before, and counts it.
	/// </summary>
	public async Task<bool> TakeAsync(Func<CancellationToken, Task<bool>> pause, CancellationToken ct) {
		if (Left <= 0) {
			return false;
		}

		if (Acted && !await pause(ct).ConfigureAwait(false)) {
			return false;
		}

		Acted = true;
		Left--;

		return true;
	}

	/// <summary>The budget for what follows a look's items: none on a robot account (null), the rest of the look's number on a human-mode one.</summary>
	public static LookBudget? After(bool human, int perLook, int done) => human ? new LookBudget(perLook - done, done > 0) : null;
}
