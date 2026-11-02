using System;
using System.Collections.Generic;
using System.Text;

namespace AirPlayTray
{
    // Minimal reader for Apple binary property lists ("bplist00"), enough for the now-playing
    // messages iOS sends. Values come back as Dictionary<string, object>, List<object>, string,
    // long, double, bool, byte[], DateTime or null.
    static class BinaryPlist
    {
        public static object Parse(byte[] b)
        {
            if (b == null || b.Length < 40 || Encoding.ASCII.GetString(b, 0, 8) != "bplist00")
                throw new FormatException("not a binary plist");
            int t = b.Length - 32;
            int offsetSize = b[t + 6];
            int refSize = b[t + 7];
            long count = ReadUInt(b, t + 8, 8);
            long top = ReadUInt(b, t + 16, 8);
            long table = ReadUInt(b, t + 24, 8);
            if (count <= 0 || count > 1_000_000 || top >= count || table + count * offsetSize > t)
                throw new FormatException("bad plist trailer");
            var offsets = new long[count];
            for (long i = 0; i < count; i++)
                offsets[i] = ReadUInt(b, (int)(table + i * offsetSize), offsetSize);
            return new Reader(b, offsets, refSize).Read(top, 0);
        }

        static long ReadUInt(byte[] b, int pos, int size)
        {
            if (pos < 0 || pos + size > b.Length) throw new FormatException("plist read out of range");
            long v = 0;
            for (int i = 0; i < size; i++) v = (v << 8) | b[pos + i];
            return v;
        }

        sealed class Reader
        {
            readonly byte[] b;
            readonly long[] offsets;
            readonly int refSize;

            public Reader(byte[] b, long[] offsets, int refSize)
            {
                this.b = b; this.offsets = offsets; this.refSize = refSize;
            }

            public object Read(long index, int depth)
            {
                if (depth > 64 || index < 0 || index >= offsets.Length) throw new FormatException("bad plist reference");
                int p = (int)offsets[index];
                if (p < 0 || p >= b.Length) throw new FormatException("bad plist offset");
                byte marker = b[p];
                int hi = marker >> 4, lo = marker & 0xF;
                switch (hi)
                {
                    case 0x0:
                        return lo == 0x8 ? false : lo == 0x9 ? (object)true : null;
                    case 0x1:
                        {
                            int n = 1 << lo;
                            long v = (long)ReadUInt(b, p + 1, Math.Min(n, 8));
                            return v;
                        }
                    case 0x2:
                        return lo == 2 ? (double)BitConverter.ToSingle(Rev(p + 1, 4), 0) : BitConverter.ToDouble(Rev(p + 1, 8), 0);
                    case 0x3:
                        return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.ToDouble(Rev(p + 1, 8), 0));
                    case 0x4:
                        {
                            int len = Length(ref p, lo);
                            Check(p, len);
                            var d = new byte[len];
                            Buffer.BlockCopy(b, p, d, 0, len);
                            return d;
                        }
                    case 0x5:
                        {
                            int len = Length(ref p, lo);
                            Check(p, len);
                            return Encoding.ASCII.GetString(b, p, len);
                        }
                    case 0x6:
                        {
                            int len = Length(ref p, lo);
                            Check(p, len * 2);
                            return Encoding.BigEndianUnicode.GetString(b, p, len * 2);
                        }
                    case 0x8:
                        return ReadUInt(b, p + 1, lo + 1);
                    case 0xA:
                        {
                            int len = Length(ref p, lo);
                            var list = new List<object>(len);
                            for (int i = 0; i < len; i++) list.Add(Read(Ref(p, i), depth + 1));
                            return list;
                        }
                    case 0xD:
                        {
                            int len = Length(ref p, lo);
                            var dict = new Dictionary<string, object>(len);
                            for (int i = 0; i < len; i++)
                            {
                                var key = Read(Ref(p, i), depth + 1) as string;
                                if (key != null) dict[key] = Read(Ref(p, len + i), depth + 1);
                            }
                            return dict;
                        }
                    default:
                        return null; // sets and other rare types are not used by AirPlay
                }
            }

            // object length: in the low nibble, or (0xF) in the int object that follows the marker.
            // Moves p to the first byte of the payload.
            int Length(ref int p, int lo)
            {
                if (lo != 0xF) { p += 1; return lo; }
                int size = 1 << (b[p + 1] & 0xF);
                long len = ReadUInt(b, p + 2, size);
                p += 2 + size;
                if (len < 0 || len > b.Length) throw new FormatException("bad plist length");
                return (int)len;
            }

            long Ref(int p, int i) => ReadUInt(b, p + i * refSize, refSize);

            void Check(int p, int len)
            {
                if (len < 0 || p + len > b.Length) throw new FormatException("plist data out of range");
            }

            byte[] Rev(int p, int n)
            {
                Check(p, n);
                var d = new byte[n];
                Buffer.BlockCopy(b, p, d, 0, n);
                if (BitConverter.IsLittleEndian) Array.Reverse(d);
                return d;
            }
        }
    }
}
