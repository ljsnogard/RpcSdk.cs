namespace RpcMuxSdk
{
    using System;
    using System.Buffers.Binary;
    using System.Threading;

    using Cysharp.Threading.Tasks;

    using BufferKit;

    using DualByte = System.UInt16;
    using QuadByte = System.UInt32;

    public readonly struct PacketFlags
    {
        /// <summary>
        /// 指示报文中表示发送端 port 要使用的数据类型, 0双字节, 1四字节
        /// </summary>
        public static readonly byte K1_B0_REPR_SRCPORT = 0b_0000_0001;

        /// <summary>
        /// 指示报文中表示接收端 port 要使用的数据类型, 0双字节, 1四字节
        /// </summary>
        public static readonly byte K1_B1_REPR_DSTPORT = 0b_0000_0010;

        /// <summary>
        /// 指示报文中表示荷载长度要使用的数据类型, 0双字节, 1四字节
        /// </summary>
        public static readonly byte K1_B2_REPR_PYLSIZE = 0b_0000_0100;

        /// <summary>
        /// 指示本数据报荷载是数据或是信令, 若为信令则置1
        /// </summary>
        public static readonly byte K1_B3_SIGN_CHANNEL = 0b_0000_1000;

        /// <summary>
        /// 指示本数据报附加头部的长度, 如无附加头部长度则为 0
        /// </summary>
        public static readonly byte K2_B4_SIGN_EXTHEAD = 0b_0011_0000;

        public readonly byte Value;

        public PacketFlags(byte value)
            => this.Value = value;

        public NUsize PacketHeaderSize
            => CalculatePacketHeaderReprSize(this.Value);

        public static NUsize CalculatePacketHeaderReprSize(byte val)
        {
            return SrcPortReprSize(val)
                + DstPortReprSize(val)
                + PayloadLenReprSize(val)
                + ExtHeaderReprSize(val);
        }

        public static bool TEST_FLAG_VAL(byte mask, byte val)
            => (mask & val) == mask;

        /// <summary>
        /// SrcPortRepr 为 1 返回 true，即 SrcPort 是 QuadByte
        /// </summary>
        public static bool TestSrcPortRepr(byte val)
            => TEST_FLAG_VAL(K1_B0_REPR_SRCPORT, val);

        /// <summary>
        /// DstPort 为 1 返回 true，即 DstPort 是 QuadByte
        /// </summary>
        public static bool TestDstPortRepr(byte val)
            => TEST_FLAG_VAL(K1_B1_REPR_DSTPORT, val);

        /// <summary>
        /// PayloadLen 为 1 返回 true，即 PayloadLen 是 QuadByte
        /// </summary>
        public static bool TestPayloadLenRepr(byte val)
            => TEST_FLAG_VAL(K1_B2_REPR_PYLSIZE, val);

        /// <summary>
        /// ChannelSignal 为 1 返回 true，即所指示报文是信令报文
        /// </summary>
        public static bool TestSignalFlag(byte val)
            => TEST_FLAG_VAL(K1_B3_SIGN_CHANNEL, val);

        public static NUsize SrcPortReprSize(byte val)
            => (NUsize)(TestSrcPortRepr(val) ? sizeof(QuadByte) : sizeof(DualByte));

        public static NUsize DstPortReprSize(byte val)
            => (NUsize)(TestDstPortRepr(val) ? sizeof(QuadByte) : sizeof(DualByte));

        public static NUsize PayloadLenReprSize(byte val)
            => (NUsize)(TestPayloadLenRepr(val) ? sizeof(QuadByte) : sizeof(DualByte));

        /// <summary>
        /// 目前没有任何附加头部的定义，不应使用
        /// </summary>
        public static NUsize ExtHeaderReprSize(byte val)
            => 0; // 暂不支持附加头部

        public static readonly NUsize MaxPacketHeaderSize_NoExtraHeader = CalculatePacketHeaderReprSize(
            (byte)(K1_B0_REPR_SRCPORT
                | K1_B1_REPR_DSTPORT
                | K1_B2_REPR_PYLSIZE
        ));

        public static readonly NUsize MinPacketHeaderSize = CalculatePacketHeaderReprSize(0);
    }

    public readonly struct PacketHeader
    {
        public readonly PacketFlags Flags;

        public readonly Port SrcPort;

        public readonly Port DstPort;

        public readonly NUsize PayloadLen;

        public readonly NUsize ExtraHeaderSize;

        public PacketHeader(
            byte flags,
            Port srcPort,
            Port dstPort,
            NUsize payloadLen,
            NUsize extraHeaderSize)
        {
            this.Flags = new PacketFlags(flags);
            this.SrcPort = srcPort;
            this.DstPort = dstPort;
            this.PayloadLen = payloadLen;
            this.ExtraHeaderSize = extraHeaderSize;
        }

        public static async UniTask<(PacketHeader, NUsize)> PeekAsync(RxProxy<byte> rx, NUsize peekOffset, CancellationToken token = default)
        {
            if (!PacketFlags.MaxPacketHeaderSize_NoExtraHeader.TryInto(out int maxHeaderSize))
                throw new NotSupportedException();

            var buffer = new byte[maxHeaderSize];
            var peekLen = NUsize.Zero;
            NUsize expectHeaderSize = 1;
            while (peekLen < expectHeaderSize)
            {
                var unfilled = buffer.Slice(peekLen, expectHeaderSize - peekLen);
                var maybePeekSize = await rx.PeekAsync(peekOffset + peekLen, unfilled, token);
                if (!maybePeekSize.TryOk(out var peekSize, out var rxErr))
                    throw rxErr.AsException();
                if (expectHeaderSize < PacketFlags.MinPacketHeaderSize)
                    expectHeaderSize = PacketFlags.CalculatePacketHeaderReprSize(buffer[0]);
                peekLen += peekSize;
            }

            var offset = NUsize.Zero;
            var flags = buffer[offset];

            offset += 1;

            var srcPortReprSize = PacketFlags.SrcPortReprSize(flags);
            var srcPortBuffer = buffer.ReadOnlySlice(offset, srcPortReprSize);
            Port srcPort;
            if (srcPortReprSize == sizeof(QuadByte))
            {
                if (!BinaryPrimitives.TryReadUInt32BigEndian(srcPortBuffer.Span, out var srcPortU32))
                    throw new Exception($"Failed parsing SrcPort with flags({flags:X8}), data({srcPortBuffer.ToArray()})");
                else
                    srcPort = new Port(srcPortU32);
            }
            else if (srcPortReprSize == sizeof(DualByte))
            {
                if (!BinaryPrimitives.TryReadUInt16BigEndian(srcPortBuffer.Span, out var srcPortU16))
                    throw new Exception($"Failed parsing SrcPort with flags({flags:X8}), data({srcPortBuffer.ToArray()})");
                else
                    srcPort = new Port(srcPortU16);
            }
            else
                throw new NotSupportedException($"Unsupported flags({flags:X8}), srcPortReprSize({srcPortReprSize})");

            offset += srcPortReprSize;

            var dstPortReprSize = PacketFlags.DstPortReprSize(flags);
            var dstPortBuffer = buffer.ReadOnlySlice(offset, dstPortReprSize);
            Port dstPort;
            if (dstPortReprSize == sizeof(QuadByte))
            {
                if (!BinaryPrimitives.TryReadUInt32BigEndian(dstPortBuffer.Span, out var dstPortU32))
                    throw new Exception($"Failed parsing DstPort with flags({flags:X8}), data({dstPortBuffer.ToArray()})");
                else
                    dstPort = new Port(dstPortU32);
            }
            else if (dstPortReprSize == sizeof(DualByte))
            {
                if (!BinaryPrimitives.TryReadUInt16BigEndian(dstPortBuffer.Span, out var dstPortU16))
                    throw new Exception($"Failed parsing DstPort with flags({flags:X8}), data({dstPortBuffer.ToArray()})");
                else
                    dstPort = new Port(dstPortU16);
            }
            else
                throw new NotSupportedException($"Unsupported flags({flags:X8}), dstPortReprSize({dstPortReprSize})");

            offset += dstPortReprSize;

            var payloadLenReprSize = PacketFlags.PayloadLenReprSize(flags);
            var payloadLenBuffer = buffer.ReadOnlySlice(offset, payloadLenReprSize);
            uint payloadLen;
            if (payloadLenReprSize == sizeof(QuadByte))
            {
                if (!BinaryPrimitives.TryReadUInt32BigEndian(payloadLenBuffer.Span, out var payloadLenU32))
                    throw new Exception($"Failed parsing PayloadLen with flags({flags:X8}), data({payloadLenBuffer.ToArray()})");
                else
                    payloadLen = payloadLenU32;
            }
            else if (payloadLenReprSize == sizeof(DualByte))
            {
                if (!BinaryPrimitives.TryReadUInt16BigEndian(payloadLenBuffer.Span, out var payloadLenU16))
                    throw new Exception($"Failed parsing PayloadLen with flags({flags:X8}), data({payloadLenBuffer.ToArray()})");
                else
                    payloadLen = payloadLenU16;
            }
            else
                throw new NotSupportedException($"Unsupported flags({flags:X8}), payloadLenReprSize({payloadLenReprSize})");

            offset += payloadLenReprSize;

            var packetHeader = new PacketHeader(
                flags,
                srcPort: srcPort,
                dstPort: dstPort,
                payloadLen: payloadLen,
                extraHeaderSize: 0
            );
            return (packetHeader, offset);
        }

        public static async UniTask<NUsize> WriteAsync<Tx>(
            PacketHeader header,
            TxProxy<byte> tx,
            CancellationToken token = default)
        {
            var buffer = new byte[PacketFlags.MaxPacketHeaderSize_NoExtraHeader];

            byte flags = 0;
            NUsize offset = 1;

            // Write SrcPort
            if (header.SrcPort.GetMinRepr().TryPickT0(out var u16SrcPort, out var u32SrcPort))
            {
                NUsize len = sizeof(DualByte);
                var buff = buffer.Slice(offset, len);
                BinaryPrimitives.WriteUInt16BigEndian(buff.Span, u16SrcPort);
                offset += len;
            }
            else
            {
                NUsize len = sizeof(QuadByte);
                var buff = buffer.Slice(offset, len);
                BinaryPrimitives.WriteUInt32BigEndian(buff.Span, u32SrcPort);
                offset += len;
                flags |= PacketFlags.K1_B0_REPR_SRCPORT;
            }

            // Write DstPort
            if (header.DstPort.GetMinRepr().TryPickT0(out var u16DstPort, out var u32DstPort))
            {
                NUsize len = sizeof(DualByte);
                var buff = buffer.Slice(offset, len);
                BinaryPrimitives.WriteUInt16BigEndian(buff.Span, u16DstPort);
                offset += len;
            }
            else
            {
                NUsize len = sizeof(QuadByte);
                var buff = buffer.Slice(offset, len);
                BinaryPrimitives.WriteUInt32BigEndian(buff.Span, u32DstPort);
                offset += len;
                flags |= PacketFlags.K1_B1_REPR_DSTPORT;
            }

            // Write PayloadLen
            if (header.PayloadLen.TryInto(out DualByte u16PayloadLen))
            {
                NUsize len = sizeof(DualByte);
                var buff = buffer.Slice(offset, len);
                BinaryPrimitives.WriteUInt16BigEndian(buff.Span, u16PayloadLen);
                offset += len;
            }
            else if (header.PayloadLen.TryInto(out QuadByte u32PayloadLen))
            {
                NUsize len = sizeof(QuadByte);
                var buff = buffer.Slice(offset, len);
                BinaryPrimitives.WriteUInt32BigEndian(buff.Span, u32PayloadLen);
                offset += len;
                flags |= PacketFlags.K1_B2_REPR_PYLSIZE;
            }
            else
                throw new InvalidCastException($"Faild to convert PayloadLen({header.PayloadLen})");

            buffer[0] = flags;
            var srcBuff = buffer.ReadOnlySlice(0, offset);

            var maybeWriteLen = await tx.DumpAsync(srcBuff, token);
            if (!maybeWriteLen.TryOk(out var writeLen, out var txErr))
                throw txErr.AsException();

            if (writeLen != offset)
                throw new Exception("Incomplete send");

            return writeLen;
        }
    }

    public readonly struct SignalFlags
    {
        public readonly byte Value;

        /// <summary>
        /// 信令的发送者希望建立 channel 或者重设 sequence
        /// </summary>
        public static readonly byte K1_B0_SIGN_SYN = 0b_0000_0001;

        /// <summary>
        /// 信令的发送者确认接收，也可用作心跳报文
        /// </summary>
        public static readonly byte K1_B1_SIGN_ACK = 0b_0000_0010;

        /// <summary>
        /// 信令的接收者拒绝在发送者指定的 Port 中接收数据。
        /// </summary>
        public static readonly byte K1_B2_SIGN_RST = 0b_0000_0100;

        /// <summary>
        /// 信令的发送者关闭 channel 或者拒绝建立 channel
        /// </summary>
        public static readonly byte K1_B3_SIGN_FIN = 0b_0000_1000;

        /// <summary>
        /// 序列号的数据类型，1 为 QuadByte
        /// </summary>
        public static readonly byte K1_B4_REPR_SEQ = 0b_0001_0000;

        /// <summary>
        /// 窗口大小的数据类型，1 为 QuadByte
        /// </summary>
        public static readonly byte K1_B5_REPR_WND = 0b_0010_0000;
    }

    public readonly struct Signal
    {
        /// <summary>
        /// 信令类型标志，SYN，ACK，RST, FIN，
        /// </summary>
        public readonly SignalFlags Flags;

        /// <summary>
        /// 确认接收的 Packet 序列号，其数据形式在网络传输中可以是 ushort
        /// </summary>
        public readonly uint Sequence;

        /// <summary>
        /// 接收端的接收窗口大小，其数据形式在网络传输中可以是 ushort
        /// </summary>
        public readonly uint Window;
    }
}