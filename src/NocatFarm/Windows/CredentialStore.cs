using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NocatFarm.Windows;

/// <summary>
/// Reading one generic credential out of the Windows Credential Manager - how Steam Game Idler keeps its accounts'
/// sign-ins. Read only: nothing here can write, change or delete a credential, and it is only called for an account
/// the user explicitly asked to bring its sign-in over for.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialStore {
	private const int CredTypeGeneric = 1;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct Credential {
		public int Flags;
		public int Type;
		public IntPtr TargetName;
		public IntPtr Comment;
		public long LastWritten;
		public int CredentialBlobSize;
		public IntPtr CredentialBlob;
		public int Persist;
		public int AttributeCount;
		public IntPtr Attributes;
		public IntPtr TargetAlias;
		public IntPtr UserName;
	}

	[DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CredRead(string target, int type, int reserved, out IntPtr credential);

	[DllImport("advapi32.dll")]
	private static extern void CredFree(IntPtr buffer);

	/// <summary>The secret bytes of the generic credential named <paramref name="target"/>, or null when there isn't one.</summary>
	public static byte[]? ReadGeneric(string target) {
		IntPtr handle = IntPtr.Zero;

		try {
			if (!CredRead(target, CredTypeGeneric, 0, out handle) || (handle == IntPtr.Zero)) {
				return null;
			}

			Credential cred = Marshal.PtrToStructure<Credential>(handle);

			if ((cred.CredentialBlobSize <= 0) || (cred.CredentialBlob == IntPtr.Zero)) {
				return null;
			}

			byte[] blob = new byte[cred.CredentialBlobSize];
			Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);

			return blob;
		} catch {
			return null;
		} finally {
			if (handle != IntPtr.Zero) {
				CredFree(handle);
			}
		}
	}
}
