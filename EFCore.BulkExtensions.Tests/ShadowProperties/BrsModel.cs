using System;

namespace EFCore.BulkExtensions.Tests.ShadowProperties;

// Models for BulkRead of entities that have non-foreign-key shadow properties (Brs = BulkRead shadow).

// TPH hierarchy: EF adds the shadow string property 'Discriminator'.
public abstract class BrsAnimal
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;
}

public class BrsDog : BrsAnimal
{
    public string Breed { get; set; } = null!;
}

public class BrsCat : BrsAnimal
{
    public int Lives { get; set; }
}

// TPH hierarchy whose discriminator is mapped to a column with a different name.
public abstract class BrsShape
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public const string Kind = nameof(Kind);
    public const string KindColumn = "shape_kind";
}

public class BrsCircle : BrsShape
{
    public int Radius { get; set; }
}

public class BrsSquare : BrsShape
{
    public int Side { get; set; }
}

// Plain (non-TPH) entity with shadow string properties, one of them mapped to a column with a different name.
public class BrsNote
{
    public int Id { get; set; }

    public string Title { get; set; } = null!;

    public const string Tag = nameof(Tag);
    public const string Comment = nameof(Comment);
    public const string CommentColumn = "comment_text";
}

// Entity whose only non-foreign-key shadow property is not a string.
public class BrsStamp
{
    public int Id { get; set; }

    public string Code { get; set; } = null!;

    public const string StampedAt = nameof(StampedAt);
}
