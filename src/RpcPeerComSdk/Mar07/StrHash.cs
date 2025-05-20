namespace RpcPeerComSdk.Mar07
{
    /// <summary>
    /// 主要用于为 Type.FullName 计算一个 Hash
    /// </summary>
    public static class StrHashExtensions
    {
        public static uint GetStableHashCode(this string str)
        {
            unchecked
            {
                uint hash1 = 5381;
                uint hash2 = hash1;

                for(int i = 0; i < str.Length && str[i] != '\0'; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ str[i];
                    if (i == str.Length - 1 || str[i+1] == '\0')
                        break;
                    hash2 = ((hash2 << 5) + hash2) ^ str[i+1];
                }

                return hash1 + (hash2*1566083941);
            }
        }
    }
}