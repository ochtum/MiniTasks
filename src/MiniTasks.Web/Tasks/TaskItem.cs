using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MiniTasks.Web.Tasks;

public sealed record TaskItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("is_completed")] bool IsCompleted,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed class NewTaskInput
{
    [Required(ErrorMessage = "タスクのタイトルを入力してください。")]
    [StringLength(200, ErrorMessage = "タイトルは200文字以内で入力してください。")]
    public string Title { get; set; } = "";
}

public sealed class TaskOperationException(string message) : Exception(message);
