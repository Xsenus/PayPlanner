namespace PayPlanner.Api.Models.Responses;

/// <summary>Компактный элемент классификатора для подсказок в карточке клиента.</summary>
public sealed record ClientReferenceItemResponse(int Id, string Code, string Name);
