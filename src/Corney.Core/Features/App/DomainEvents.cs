using System;
using MediatR;

namespace Corney.Features.App;

public record AppStartingEvent : INotification;
public class StartCorneyReq : INotification
{
    public string[] CrontabFiles { get; }

    public StartCorneyReq(string[] crontabFiles)
    {
        CrontabFiles = crontabFiles;
    }
}

