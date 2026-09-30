namespace SearchService.IntegrationTests.Eval;

// A fixed synthetic organization and labelled queries for measuring search quality. Written before looking at any
// results and not tuned afterwards: the point is to compare modes on a set that was not shaped to favour one.
//
// Three kinds of query, because they stress different things:
//   Word      - shares a word with the name (what keyword search is for)
//   Meaning   - describes the thing with no word in common with its name (what keyword search cannot do)
//   Typo      - the name with a slip in it (what a semantic model may or may not forgive)
// Relevant is the set of names that count as a correct answer; more than one where the org really has near-duplicates.
public static class EvalCorpus
{
    public enum QueryKind
    {
        Word,
        Meaning,
        Typo,
    }

    public sealed record EvalQuery(string Text, QueryKind Kind, string Type, string[] Relevant);

    public static readonly string[] Departments =
    [
        "Payments", "Billing", "Accounts Payable", "Accounts Receivable", "Treasury", "Payroll", "Tax", "Compliance",
        "Legal", "Risk Management", "Fraud Prevention", "Customer Support", "Customer Success", "Customer Onboarding",
        "Sales", "Enterprise Sales", "Partnerships", "Marketing", "Brand Design", "Content", "Social Media",
        "UX Research", "Product Management", "Data Science", "Analytics", "Data Engineering", "Machine Learning",
        "Platform Engineering", "Infrastructure", "Site Reliability", "Security Engineering", "Mobile Development",
        "Web Development", "Quality Assurance", "DevOps", "IT Support", "Facilities", "Office Management",
        "Procurement", "Supply Chain", "Logistics", "Warehouse", "Manufacturing", "Quality Control",
        "Research and Development", "Talent Acquisition", "Learning and Development", "Employee Relations",
        "Compensation and Benefits", "Diversity and Inclusion", "Corporate Communications", "Public Relations",
        "Investor Relations", "Strategy", "Business Development", "Internal Audit", "Executive Office",
        "Customer Insights", "Internal Tools", "Localization",
    ];

    public static readonly string[] Positions =
    [
        "Software Engineer", "Data Analyst", "Accountant", "HR Manager", "Customer Support Agent", "Security Analyst",
        "Product Designer", "Sales Representative", "DevOps Engineer", "Recruiter", "Financial Controller",
        "Legal Counsel",
    ];

    public static readonly EvalQuery[] Queries =
    [
        // Word: a word of the name is in the query.
        new("payroll", QueryKind.Word, "department", ["Payroll"]),
        new("legal", QueryKind.Word, "department", ["Legal"]),
        new("marketing team", QueryKind.Word, "department", ["Marketing"]),
        new("data science", QueryKind.Word, "department", ["Data Science"]),
        new("mobile", QueryKind.Word, "department", ["Mobile Development"]),
        new("security", QueryKind.Word, "department", ["Security Engineering"]),
        new("treasury", QueryKind.Word, "department", ["Treasury"]),
        new("audit", QueryKind.Word, "department", ["Internal Audit"]),
        new("customer support", QueryKind.Word, "department", ["Customer Support"]),
        new("procurement", QueryKind.Word, "department", ["Procurement"]),
        new("software engineer", QueryKind.Word, "position", ["Software Engineer"]),
        new("recruiter", QueryKind.Word, "position", ["Recruiter"]),

        // Meaning: describes it; no word of the query is in the name.
        new("people who pay our suppliers", QueryKind.Meaning, "department", ["Accounts Payable"]),
        new("collecting money customers owe us", QueryKind.Meaning, "department", ["Accounts Receivable"]),
        new("hiring new employees", QueryKind.Meaning, "department", ["Talent Acquisition"]),
        new("keeping the servers running", QueryKind.Meaning, "department", ["Site Reliability", "Infrastructure"]),
        new("protecting us from hackers", QueryKind.Meaning, "department", ["Security Engineering"]),
        new("training courses for staff", QueryKind.Meaning, "department", ["Learning and Development"]),
        new("wages and salaries", QueryKind.Meaning, "department", ["Payroll", "Compensation and Benefits"]),
        new("stopping scams and stolen cards", QueryKind.Meaning, "department", ["Fraud Prevention"]),
        new("shipping goods to buyers", QueryKind.Meaning, "department", ["Logistics", "Supply Chain"]),
        new("buying materials and negotiating with vendors", QueryKind.Meaning, "department", ["Procurement"]),
        new("talking to journalists", QueryKind.Meaning, "department", ["Public Relations", "Corporate Communications"]),
        new("shareholders and stock analysts", QueryKind.Meaning, "department", ["Investor Relations"]),
        new("apps for phones", QueryKind.Meaning, "department", ["Mobile Development"]),
        new("finding bugs before release", QueryKind.Meaning, "department", ["Quality Assurance"]),
        new("interviewing users about the product", QueryKind.Meaning, "department", ["UX Research"]),
        new("predictive models and neural networks", QueryKind.Meaning, "department", ["Machine Learning", "Data Science"]),
        new("cleaning and maintaining the building", QueryKind.Meaning, "department", ["Facilities"]),
        new("helping users who have a problem", QueryKind.Meaning, "department", ["Customer Support"]),
        new("person who writes code", QueryKind.Meaning, "position", ["Software Engineer"]),
        new("crunches numbers and builds reports", QueryKind.Meaning, "position", ["Data Analyst"]),
        new("keeps the books", QueryKind.Meaning, "position", ["Accountant"]),
        new("finds and interviews candidates", QueryKind.Meaning, "position", ["Recruiter"]),
        new("reviews contracts and gives legal advice", QueryKind.Meaning, "position", ["Legal Counsel"]),

        // Typo: the name with a slip.
        new("paymnts", QueryKind.Typo, "department", ["Payments"]),
        new("complience", QueryKind.Typo, "department", ["Compliance"]),
        new("logistcs", QueryKind.Typo, "department", ["Logistics"]),
        new("marketting", QueryKind.Typo, "department", ["Marketing"]),
        new("ananlytics", QueryKind.Typo, "department", ["Analytics", "Customer Insights"]),
        new("sercurity", QueryKind.Typo, "department", ["Security Engineering"]),
        new("procuremnt", QueryKind.Typo, "department", ["Procurement"]),
        new("recuiter", QueryKind.Typo, "position", ["Recruiter"]),
    ];
}
