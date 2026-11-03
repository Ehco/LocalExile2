using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Instance;

/// <summary>PoE2 instance-connection messages that differ from <see cref="InstanceProtocol"/> (PORTING-POE2.md, wave 8).</summary>
public static class InstanceProtocolPoe2
{
    /// <summary>
    /// S→C 0x11 on PoE2: PoE1's encryption switch plus one byte, read in plaintext before the switch. The dispatcher
    /// (0x1421220f0, case at 0x1421221be) does not use it: it builds the Salsa20 pair from the 0x14 key (info +0x38) with
    /// the client flag, as PoE1 and the login connection do, so <see cref="InstanceKeyring.Ciphers"/> stays correct.
    /// </summary>
    public static byte[] StartEncryption(byte u8 = 0) => new LoginWriter().U16(InstanceProtocol.StartEncryptionId).U8(u8).ToArray();
}
