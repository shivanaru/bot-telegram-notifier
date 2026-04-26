public class JobConfig
{
    public List<JobSource> JobSources { get; set; }
    public JobFilters Filters { get; set; }
}

public class JobSource
{
    public string Name { get; set; }
    public string Url { get; set; }
}

public class JobFilters
{
    public List<string> IncludeKeywords { get; set; }
    public List<string> ExcludeKeywords { get; set; }
}