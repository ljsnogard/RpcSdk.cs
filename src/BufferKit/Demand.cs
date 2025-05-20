namespace BufferKit
{
    public readonly struct Demand
    {
        private readonly NUsize floor_;

        private readonly NUsize ceiling_;

        private readonly uint flags_;

        private const uint K_FLOOR_VALID = 2u;

        private const uint K_CEILING_VALID = 3u;

        private const uint K_BOTH_VALID = 6u;

        private Demand(NUsize floor, NUsize ceiling, uint flags)
        {
            this.floor_ = floor;
            this.ceiling_ = ceiling;
            this.flags_ = flags;
        }

        public Option<NUsize> Floor
            => this.flags_ % K_FLOOR_VALID == 0 ? Option.Some(this.floor_) : Option.None;

        public Option<NUsize> Ceiling
            => this.flags_ % K_CEILING_VALID == 0 ? Option.Some(this.ceiling_) : Option.None;

        public override string ToString()
        {
            System.Text.StringBuilder sb = new();
            sb.Append(nameof(Demand));
            sb.Append("{");
            if (this.Floor.IsSome(out var floor))
                sb.Append($"Floor: {floor}");
            if (this.Ceiling.IsSome(out var ceiling))
            {
                if (this.Floor.IsSome())
                    sb.Append(", ");
                sb.Append($"Ceiling: {ceiling}");
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static NUsize MinFloor
            => (NUsize)1u;

        public static bool IsLegal(in Demand demand)
        {
            if (demand.flags_ == K_BOTH_VALID || demand.flags_ == K_FLOOR_VALID || demand.flags_ == K_CEILING_VALID)
            {
                if (demand.flags_ == K_BOTH_VALID && demand.floor_ > demand.ceiling_)
                    return false;
                return true;
            }
            return false;
        }

        public static readonly Demand Least = Demand.AtLeast(MinFloor);

        public static Demand Exactly(NUsize count)
            => new Demand(count, count, K_BOTH_VALID);

        public static Demand AtLeast(NUsize floor)
            => new Demand(floor: NUsize.Max(MinFloor, floor), ceiling: default, K_FLOOR_VALID);

        public static Demand AtMost(NUsize ceiling)
            => new Demand(floor: MinFloor, ceiling, K_CEILING_VALID);

        public static Demand Between(NUsize floor, NUsize ceiling)
        {
            if (floor > ceiling)
                return new Demand(ceiling, floor, K_BOTH_VALID);
            else
                return new Demand(floor, ceiling, K_BOTH_VALID);
        }
    }
}