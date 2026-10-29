export interface YoutubeCandidateInfo {
 id:string;label:string;pageConfirmed:boolean;mediaConfirmed:boolean;fullyConfirmed:boolean;canApply:boolean;samples:number;latencyMs?:number;
 groups:{id:string;state:string;detail:string}[];source?:'current'|'reserve'|'ai';pageMs?:number;
}
export interface YoutubeAdvisorInfo {
 state:string;runId?:string;revision?:number;startedAt?:number;profileId?:string;message?:string;error?:string;
 readiness?:string;blockedReason?:string|null;elapsedMs?:number;remainingMs?:number;backupDirectory?:string;firefox?:string;candidates?:YoutubeCandidateInfo[];
}
export function mergeYoutubeStatus(current:YoutubeAdvisorInfo,message:YoutubeAdvisorInfo):YoutubeAdvisorInfo {
 if(!message.runId||message.runId!==current.runId)return current;
 if((message.revision??0)<=(current.revision??0))return current;
 if(message.state==='readiness')return {...current,revision:message.revision,readiness:message.readiness,blockedReason:message.blockedReason};
 return {...current,...message,error:message.state==='applying'||message.state==='testing'?undefined:message.error};
}
