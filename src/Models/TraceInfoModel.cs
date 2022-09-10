namespace PerfViewJS;

public class TraceInfoModel
{
    public string Filename { get; set; }
    public string StackType { get; set; }
    public string Pid { get; set; }
    public string Start { get; set; }
    public string End { get; set; }
    public string GroupPats { get; set; }
    public string IncPats { get; set; }
    public string ExcPats { get; set; }
    public string FoldPats { get; set; }
    public string FoldPct { get; set; }
    public string DrillIntoKey { get; set; }

}