// FreeFlume — a single video chapter (start time + title), from mpv's chapter-list.
// Author: velkadyne

namespace FreeFlume.Models;

public sealed record Chapter(double Start, string Title);
