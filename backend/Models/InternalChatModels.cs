using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class InternalChatThreadKinds
{
    public const string Task = "task";
    public const string Channel = "channel";
    public const string Direct = "direct";
}

public static class InternalChatNotificationModes
{
    public const string All = "all";
    public const string Mentions = "mentions";
    public const string Muted = "muted";
}

public static class InternalChatMessageKinds
{
    public const string User = "user";
    public const string System = "system";
    public const string TaskLink = "task-link";
}

public class InternalChatThread
{
    public int Id { get; set; }

    [Required, MaxLength(24)]
    public string Kind { get; set; } = InternalChatThreadKinds.Channel;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public int? TaskJournalItemId { get; set; }
    public TaskJournalItem? TaskJournalItem { get; set; }

    [MaxLength(64)]
    public string? DirectKey { get; set; }

    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public bool IsArchived { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<InternalChatMember> Members { get; set; } = new List<InternalChatMember>();
    public ICollection<InternalChatMessage> Messages { get; set; } = new List<InternalChatMessage>();
}

public class InternalChatMember
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public InternalChatThread Thread { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    [Required, MaxLength(24)]
    public string Role { get; set; } = "member";

    [Required, MaxLength(24)]
    public string NotificationMode { get; set; } = InternalChatNotificationModes.All;

    public int? LastReadMessageId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsArchived { get; set; }
    public int? HiddenAfterMessageId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public class InternalChatMessage
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    public InternalChatThread Thread { get; set; } = null!;
    public int? AuthorUserId { get; set; }
    public User? AuthorUser { get; set; }

    [Required, MaxLength(24)]
    public string Kind { get; set; } = InternalChatMessageKinds.User;

    [MaxLength(8000)]
    public string Text { get; set; } = string.Empty;

    public int? ReplyToMessageId { get; set; }
    public InternalChatMessage? ReplyToMessage { get; set; }
    public int? CreatedTaskJournalItemId { get; set; }
    public TaskJournalItem? CreatedTaskJournalItem { get; set; }

    public bool IsPinned { get; set; }
    public int? PinnedByUserId { get; set; }
    public User? PinnedByUser { get; set; }
    public DateTime? PinnedAt { get; set; }
    public DateTime? EditedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<InternalChatAttachment> Attachments { get; set; } = new List<InternalChatAttachment>();
    public ICollection<InternalChatReaction> Reactions { get; set; } = new List<InternalChatReaction>();
    public ICollection<InternalChatMention> Mentions { get; set; } = new List<InternalChatMention>();
}

public class InternalChatAttachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public InternalChatMessage Message { get; set; } = null!;

    [Required, MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string StoredFileName { get; set; } = string.Empty;

    [Required, MaxLength(160)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long Size { get; set; }
    public int UploadedByUserId { get; set; }
    public User UploadedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class InternalChatReaction
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public InternalChatMessage Message { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    [Required, MaxLength(32)]
    public string Emoji { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class InternalChatMention
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public InternalChatMessage Message { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
}
