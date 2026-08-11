using System.Numerics;
using ChatTwo.GameFunctions.Types;

namespace ChatTwo.Ui;

public interface IChatWindow
{
    Vector2 LastWindowPos { get; set; }
    Vector2 LastWindowSize { get; set; }
    HideState CurrentHideState { get; set; }
}