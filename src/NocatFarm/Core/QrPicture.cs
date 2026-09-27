using Net.Codecrete.QrCodeGenerator;

namespace NocatFarm.Core;

/// <summary>
/// A QR code as an SVG picture, for the dashboard - used to sign in with the Steam mobile app instead of a password.
/// </summary>
public static class QrPicture {
	/// <summary>An SVG of <paramref name="text"/>, dark on light, with the quiet border scanners need.</summary>
	public static string Svg(string text) => QrCode.EncodeText(text, QrCode.Ecc.Medium).ToSvgString(4);
}
