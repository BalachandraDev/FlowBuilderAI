namespace FlowCapture.Domain;

public enum ProjectStatus { Active, OnHold, Completed }
/// <summary>Collaborator: a colleague assigned to the same client. Can open the project and add notes and documents, but cannot change or delete the project.</summary>
public enum ProjectRole { Executive, Lead, Reviewer, Collaborator }

public enum FlowStatus { Draft, Analysed, UnderReview, Confirmed, DocumentsGenerated }

/// <summary>Image: a photo of handwritten or printed notes. The AI reads it into text, which is then treated like a transcript.</summary>
public enum EntryKind { Text, Voice, Image }
public enum UploadState { Initiated, Uploading, Complete, Failed }
public enum TranscriptStatus { Queued, Processing, Ready, Failed }

public enum AnalysisScope { Part, Whole }
public enum AnalysisMode { Initial, Reanalyse }
public enum AnalysisStatus { Queued, Running, Succeeded, Failed, Cancelled }
public enum VersionSource { Analysis, Manual, Rollback }

/// <summary>What a request is: a New Requirement (NR), a Change Request (CR) or a Bug to fix (BUG) in a module. Stored as text, so no database change was needed for BUG.</summary>
public enum RequestKind { NR, CR, BUG }

public enum ClarificationStatus { Open, Answered, Dismissed }
public enum LinkStatus { Proposed, Accepted, Rejected }

/** What an AI call was for. Shown on the AI usage page. */
public enum AiUsageKind { Analysis, Guide, Voice, Photo, Test }

public enum DiagramKind { Flow, Combined }
/// <summary>Flow and Project are the Word requirement documents; Guide is the developer implementation guide (PDF).</summary>
/// <summary>Guide: the implementation document of one request. GuideModule: one PDF of every request in a module. GuideProject: one big PDF of the whole project.</summary>
public enum DocumentScope { Flow, Project, Guide, GuideModule, GuideProject }
public enum ApprovalStatus { Pending, Approved, Rejected }
public enum DocJobStatus { Queued, Running, Succeeded, Failed }
