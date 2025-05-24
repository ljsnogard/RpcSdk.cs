namespace NsBufferKit.NsUtils
{
    using System;

    public static class StrHashExtensions
    {
        public static uint GetStableHashCode(this string str)
        {
            unchecked
            {
                uint hash1 = 5381;
                uint hash2 = hash1;

                for (int i = 0; i < str.Length && str[i] != '\0'; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0')
                        break;
                    hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                }
                return hash1 + (hash2 * 1566083941);
            }
        }

        public static uint GetTypeHex(this Type type)
            => type.FullName.GetStableHashCode();

        public static uint GetTypeHexWithRound(this Type type, uint round)
        {
            var name = type.FullName;
            var hex = type.GetTypeHex();
            while (round != 0)
            {
                name = name + $"_{hex:X8}";
                hex = name.GetStableHashCode();
                round -= 1u;
            }
            return hex;
        }
    }
}
