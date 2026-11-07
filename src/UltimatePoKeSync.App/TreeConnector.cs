using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UltimatePoKeSync.App;

/// <summary>
/// The lines of the Battle tab's tree diagrams (D-081): one node on one side, <see cref="Count"/>
/// stacked branches of equal height on the other. A split fans out from the left-hand node to
/// the branches; a merge gathers the branches into the right-hand result.
/// </summary>
public sealed class TreeConnector : Control
{
    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<TreeConnector, int>(nameof(Count), 1);

    public static readonly StyledProperty<bool> MergeProperty =
        AvaloniaProperty.Register<TreeConnector, bool>(nameof(Merge));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TreeConnector, IBrush?>(nameof(Stroke));

    static TreeConnector() => AffectsRender<TreeConnector>(CountProperty, MergeProperty, StrokeProperty);

    /// <summary>How many branches: their centres sit at equal fractions of the height.</summary>
    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Branches on the left joining into one on the right, instead of the reverse.</summary>
    public bool Merge
    {
        get => GetValue(MergeProperty);
        set => SetValue(MergeProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Count < 1 || Stroke is null)
        {
            return;
        }

        double width = Bounds.Width;
        double height = Bounds.Height;
        double middle = width / 2;
        var pen = new Pen(Stroke, 2, lineCap: PenLineCap.Round);

        double Centre(int index) => height * ((2 * index) + 1) / (2 * Count);

        // The single node's side and the branches' side.
        double nodeX = Merge ? width : 0;
        double branchX = Merge ? 0 : width;

        context.DrawLine(pen, new Point(nodeX, height / 2), new Point(middle, height / 2));
        context.DrawLine(pen, new Point(middle, Centre(0)), new Point(middle, Centre(Count - 1)));

        for (int index = 0; index < Count; index++)
        {
            context.DrawLine(pen, new Point(middle, Centre(index)), new Point(branchX, Centre(index)));
        }
    }
}
