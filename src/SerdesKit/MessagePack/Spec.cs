namespace SerdesKit.MessagePack
{
    public static class Spec
    {
        public const byte Nil = 0xC0;
        public const byte False = 0xC2;
        public const byte True = 0xC3;

        public const byte U8 = 0xCC;
        public const byte U16 = 0xCD;
        public const byte U32 = 0xCE;
        public const byte U64 = 0xCF;

        public static bool TryFixint(
            byte data,
            out byte fixint,
            out ushort u8BigEndian)
        {
            if (data <= 0x7f)
            {
                fixint = data;
                u8BigEndian = default;
                return true;
            }
            else
            {
                fixint = default;
                u8BigEndian = unchecked((ushort) (((ushort)U8) << 8 + (ushort)data));
                return false;
            }
        }

        /// <summary>
        /// int 8 stores a 8-bit signed integer
        /// </summary>
        public const byte I8 = 0xD0;

        /// <summary>
        /// int 16 stores a 16-bit big-endian signed integer
        /// </summary>
        public const byte I16 = 0xD1;

        /// <summary>
        /// int 32 stores a 32-bit big-endian signed integer
        /// </summary>
        public const byte I32 = 0xD2;

        /// <summary>
        /// int 64 stores a 64-bit big-endian signed integer
        /// </summary>
        public const byte I64 = 0xD3;

        /// <summary>
        /// float 32 stores a floating point number in IEEE 754 single precision floating point number format:
        /// </summary>
        public const byte F32 = 0xCA;

        /// <summary>
        /// float 64 stores a floating point number in IEEE 754 double precision floating point number format:
        /// </summary>
        public const byte F64 = 0xCB;

        /// <summary>
        /// str 8 stores a byte array whose length is upto (2^8)-1 bytes:
        /// </summary>
        public const byte Str8 = 0xD9;

        /// <summary>
        /// str 16 stores a byte array whose length is upto (2^16)-1 bytes:
        /// </summary>
        public const byte Str16 = 0xDA;

        /// <summary>
        /// str 32 stores a byte array whose length is upto (2^32)-1 bytes:
        /// </summary>
        public const byte Str32 = 0xDB;

        /// <summary>
        /// bin 8 stores a byte array whose length is upto (2^8)-1 bytes:
        /// </summary>
        public const byte Bin8 = 0xc4;

        public const byte Bin16 = 0xc5;

        public const byte Bin32 = 0xc6;

        public const byte Arr16 = 0xdc;
        public const byte Arr32 = 0xdd;

        public const byte Map16 = 0xde;
        public const byte Map32 = 0xdf;

        /// <summary>
        /// timestamp 32 stores the number of seconds that have elapsed since 1970-01-01 00:00:00 UTC in an 32-bit unsigned integer:
        /// </summary>
        public const byte Time32 = 0xd6;

        /// <summary>
        /// timestamp 64 stores the number of seconds and nanoseconds that have elapsed since 1970-01-01 00:00:00 UTC in 32-bit unsigned integers:
        /// </summary>
        public const byte Time64 = 0xd7;

        /// <summary>
        /// timestamp 96 stores the number of seconds and nanoseconds that have elapsed since 1970-01-01 00:00:00 UTC in 64-bit signed integer and 32-bit unsigned integer:
        /// </summary>
        public const byte Time96 = 0xc7;
    }
}