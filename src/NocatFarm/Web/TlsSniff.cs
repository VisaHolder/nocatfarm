using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using NocatFarm.Core;

namespace NocatFarm.Web;

/// <summary>
/// 1.8.6: HTTPS and plain HTTP on the dashboard's one port. A connection that opens with a TLS hello (its first byte 0x16)
/// is answered over TLS with the dashboard's own certificate (<see cref="DashboardCert"/>); anything else carries on as
/// plain HTTP - the box's watcher and TV view, Docker's health check and every script that asks http://127.0.0.1 keep
/// working, and nobody has to learn a second port. Whether plain HTTP is let in from where it came is the dashboard's
/// call (WebHost.Refusal: never from the internet).
/// </summary>
internal static class TlsSniff {
	/// <summary>The connection step, for Kestrel's endpoint defaults. <paramref name="cert"/> is asked per connection - a certificate made again is used at once.</summary>
	public static Func<ConnectionDelegate, ConnectionDelegate> Middleware(Func<X509Certificate2> cert) => next => async ctx => {
		PipeReader input = ctx.Transport.Input;
		ReadResult first;

		// 1.8.6: a socket that never sends a byte is let go after 15 seconds - Kestrel's own timeouts only start after this step
		using (CancellationTokenSource firstByte = CancellationTokenSource.CreateLinkedTokenSource(ctx.ConnectionClosed)) {
			firstByte.CancelAfter(TimeSpan.FromSeconds(15));

			try {
				first = await input.ReadAsync(firstByte.Token).ConfigureAwait(false);
			} catch (Exception e) when (e is OperationCanceledException or IOException or ConnectionResetException) {
				return;
			}
		}

		if (first.Buffer.IsEmpty) {
			input.AdvanceTo(first.Buffer.Start);

			if (!first.IsCompleted) {
				await next(ctx).ConfigureAwait(false);
			}

			return;
		}

		bool tls = first.Buffer.FirstSpan[0] == 0x16;   // TLS record type "handshake" - no HTTP request starts with it
		input.AdvanceTo(first.Buffer.Start);   // nothing taken: plain HTTP reads it all again

		if (!tls) {
			await next(ctx).ConfigureAwait(false);

			return;
		}

		IDuplexPipe plain = ctx.Transport;
		SslStream ssl = new(new PipeStream(plain), leaveInnerStreamOpen: false);

		try {
			using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(ctx.ConnectionClosed);
			handshake.CancelAfter(TimeSpan.FromSeconds(15));
			await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
				ServerCertificate = cert(),
				EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
				ApplicationProtocols = [SslApplicationProtocol.Http11],
				ClientCertificateRequired = false
			}, handshake.Token).ConfigureAwait(false);
		} catch (Exception e) {   // 1.8.6: whatever went wrong (a certificate that couldn't be made too) ends this connection only
			// A browser that doesn't trust the certificate yet ends the handshake itself - not worth more than a debug line.
			Log.DebugOnChange("dashboard:tls", $"dashboard: an HTTPS visitor left during the handshake ({e.GetType().Name})");
			await ssl.DisposeAsync().ConfigureAwait(false);

			return;
		}

		ctx.Features.Set<ITlsConnectionFeature>(new Tls());   // Kestrel reads it: the request's scheme is https
		ctx.Transport = new Pipe(PipeReader.Create(ssl), PipeWriter.Create(ssl));

		try {
			await next(ctx).ConfigureAwait(false);
		} finally {
			ctx.Transport = plain;
			await ssl.DisposeAsync().ConfigureAwait(false);
		}
	};

	private sealed class Tls : ITlsConnectionFeature {
		public X509Certificate2? ClientCertificate { get; set; }

		public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult<X509Certificate2?>(null);
	}

	private sealed class Pipe(PipeReader input, PipeWriter output) : IDuplexPipe {
		public PipeReader Input { get; } = input;
		public PipeWriter Output { get; } = output;
	}

	/// <summary>The connection's pipes as one stream, for <see cref="SslStream"/>.</summary>
	private sealed class PipeStream(IDuplexPipe pipe) : Stream {
		private readonly Stream _in = pipe.Input.AsStream(leaveOpen: true), _out = pipe.Output.AsStream(leaveOpen: true);

		public override bool CanRead => true;
		public override bool CanWrite => true;
		public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _in.ReadAsync(buffer, offset, count, cancellationToken);
		public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _out.WriteAsync(buffer, cancellationToken);
		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _out.WriteAsync(buffer, offset, count, cancellationToken);
		public override void Flush() => _out.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => _out.FlushAsync(cancellationToken);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
	}
}
