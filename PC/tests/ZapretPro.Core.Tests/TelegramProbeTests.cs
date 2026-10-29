using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZapretPro.Core.Health;

namespace ZapretPro.Core.Tests;

public class TelegramProbeTests
{
    [Fact]
    public void AesCtr_matches_nist_vector()
    {
        // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt
        var ctr = new AesCtr(
            Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4"),
            Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"));

        // По кусочкам разной длины: состояние счётчика должно переживать границы вызовов
        var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
        var cipher = ctr.Transform(plain.AsSpan(0, 5)).Concat(ctr.Transform(plain.AsSpan(5))).ToArray();

        Assert.Equal("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5", Convert.ToHexString(cipher).ToLowerInvariant());
    }

    [Fact]
    public async Task Probe_succeeds_against_server_speaking_obfuscated2()
    {
        var secret = RandomNumberGenerator.GetBytes(16);
        using var server = new FakeMtprotoServer(secret);

        var problem = await TelegramProbe.CheckAsync("127.0.0.1", server.Port, Convert.ToHexString(secret), 2, TimeSpan.FromSeconds(5), default);

        Assert.Null(problem);
        Assert.Equal(2, server.Dc);
    }

    [Fact]
    public async Task Probe_fails_with_wrong_secret()
    {
        using var server = new FakeMtprotoServer(RandomNumberGenerator.GetBytes(16));

        var problem = await TelegramProbe.CheckAsync("127.0.0.1", server.Port, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), 2,
            TimeSpan.FromSeconds(5), default);

        Assert.NotNull(problem);
    }

    [Fact]
    public async Task Probe_reports_closed_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var problem = await TelegramProbe.CheckAsync("127.0.0.1", port, Convert.ToHexString(new byte[16]), 2, TimeSpan.FromSeconds(3), default);

        Assert.Contains("не слушает", problem);
    }

    [Fact]
    public void Handshake_never_looks_like_other_protocols()
    {
        for (var i = 0; i < 2000; i++)
        {
            var (init, _, _) = TelegramProbe.CreateHandshake(new byte[16], 2);
            Assert.NotEqual(0xef, init[0]);
            Assert.NotEqual(0x16, init[0]); // иначе tg-ws-proxy примет это за TLS
            Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(4)));
        }
    }

    /// <summary>
    /// Сервер, который принимает соединение так же, как tg-ws-proxy (proxy/tg_ws_proxy.py: _try_handshake,
    /// _build_crypto_ctx), и отвечает на req_pq_multi сообщением resPQ.
    /// </summary>
    private sealed class FakeMtprotoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _secret;

        public FakeMtprotoServer(byte[] secret)
        {
            _secret = secret;
            _listener.Start();
            _ = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Dc { get; private set; }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var init = new byte[64];
            await stream.ReadExactlyAsync(init);

            var prekeyIv = init.AsSpan(8, 48).ToArray();
            var decryptor = new AesCtr(SHA256.HashData([.. prekeyIv.AsSpan(0, 32), .. _secret]), prekeyIv.AsSpan(32, 16).ToArray());
            var decrypted = decryptor.Transform(init);
            if (BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan(56)) is not (0xeeeeeeee or 0xdddddddd or 0xefefefef))
                return; // как tg-ws-proxy: чужой секрет — молча не отвечаем
            Dc = BinaryPrimitives.ReadInt16LittleEndian(decrypted.AsSpan(60));

            Array.Reverse(prekeyIv);
            var encryptor = new AesCtr(SHA256.HashData([.. prekeyIv.AsSpan(0, 32), .. _secret]), prekeyIv.AsSpan(32, 16).ToArray());

            var lengthBytes = new byte[4];
            await stream.ReadExactlyAsync(lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(decryptor.Transform(lengthBytes));
            var request = new byte[length];
            await stream.ReadExactlyAsync(request);
            request = decryptor.Transform(request);
            if (BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(20)) != 0xbe7e8ef1)
                return;

            var response = new byte[8 + 8 + 4 + 4 + 16 + 16 + 12];
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(16), response.Length - 20);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(20), 0x05162463);
            request.AsSpan(24, 16).CopyTo(response.AsSpan(24));
            var frame = new byte[4 + response.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, response.Length);
            response.CopyTo(frame, 4);
            await stream.WriteAsync(encryptor.Transform(frame));
            await Task.Delay(500);
        }

        public void Dispose() => _listener.Stop();
    }
}
