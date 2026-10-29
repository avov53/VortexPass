import {useEffect,useState} from 'react';
import {Button} from '@openai/apps-sdk-ui/components/Button';
import ProfileOverlay from './ProfileOverlay';
import {postNativeMessage,postNativePayload} from '../NativeBridge';
import type {ConnectionProfile,NativeState} from '../NativeState';
export default function YoutubeAdvisorPanel({app,profile,onClose}:{app:NativeState;profile:ConnectionProfile;onClose:()=>void}){
 const status=app.youtubeAdvisor;const own=status.profileId===profile.id;
 const busy=own&&['testing','restoring','applying'].includes(status.state);
 const [chosen,setChosen]=useState('');const [confirm,setConfirm]=useState(false);
 const [invalidatedRun,setInvalidatedRun]=useState<string>();
 const selected=own?status.candidates?.find(c=>c.id===chosen):undefined;
 const connected=app.protection&&app.connections.active?.id===profile.id;
 const results=own&&['complete','applyFailed'].includes(status.state);
 useEffect(()=>{setChosen('');setConfirm(false)},[status.runId]);
 useEffect(()=>{if(results&&!connected&&!app.controlsBusy)setInvalidatedRun(status.runId)},[results,connected,app.controlsBusy,status.runId]);
 const stale=invalidatedRun===status.runId&&!!status.runId;
 const blocked=!connected?'Подключите профиль и повторите проверку YouTube':stale?'Соединение менялось. Нужна повторная проверка':app.controlsBusy?'Другая операция ещё выполняется':status.blockedReason;
 const canApply=results&&!!selected?.canApply&&!blocked&&!busy;
 const label=(value:string)=>value==='personalized'?'Индивидуальный YouTube':value==='direct'?'YouTube без DPI':value==='youtube-segment'?'YouTube · чистое разбиение TCP':value==='youtube-tls'?'YouTube TLS · резерв':value;
 const close=()=>{if(busy)postNativeMessage('advisor.stop');onClose()};
 const apply=(allowPartial:boolean)=>{if(!canApply)return;setConfirm(false);postNativePayload({type:'youtube.advisor.apply',runId:status.runId,id:chosen,allowPartial})};
 return <><ProfileOverlay title="Подбор только YouTube" drawer onClose={close}>
 <div className="dialog-body youtube-only-panel"><h3>{profile.name}</h3>
 <p>Сохраняем рабочие Telegram, Discord, WARP, DNS и игровые правила. ИИ предлагает параметры по измерениям вашей сети; youtube-tls остаётся резервом.</p>
 <p className="rule-note">Испытания кратко перезапускают основной winws и могут прерывать DPI-защиту. WARP bootstrap и PAC не переподключаются. Не запускайте подбор во время важного звонка или игры.</p>
 {!connected&&!busy&&<div role="status"><p className="field-error">{app.controlsBusy?'Дождитесь завершения подключения или текущей проверки.':app.protection?`Сейчас активно другое подключение${app.connections.active?` «${app.connections.active.name}»`:''}. Для проверки нужен профиль «${profile.name}».`:`Профиль «${profile.name}» сейчас не подключён.`}</p>
 
 <p className="rule-note">После подключения повторите проверку. Старые результаты не применяются к новому соединению.</p></div>}
 {own&&<><p role="status">{status.message}</p>{status.error&&<p className="field-error" role="alert">{status.error}</p>}
 {busy&&<p className="rule-note">Прошло {Math.floor((status.elapsedMs??0)/1000)} с · осталось {Math.ceil((status.remainingMs??0)/1000)} с</p>}
 {status.backupDirectory&&<details><summary>Резервная копия</summary><p className="rule-note">{status.backupDirectory}</p></details>}
 {results&&<><p className="rule-note">Страница и медиа подтверждаются отдельно. Выбор применяется только после нажатия кнопки и свежей проверки.</p>
 {status.candidates?.map(c=><article className="youtube-result" key={c.id}><label><input type="radio" name="youtube-result" value={c.id} checked={chosen===c.id} disabled={!c.canApply||busy} onChange={()=>setChosen(c.id)}/> {label(c.label)} · {c.samples} замер(а)</label>
 <p>Страница: {c.pageConfirmed?'подтверждена':'не подтверждена'} · Медиа: {c.mediaConfirmed?'подтверждены':'не подтверждены'} · Firefox: нужна проверка</p>
 <p className="rule-note">{c.source==='current'?'Исходный маршрут':c.source==='reserve'?'Встроенный резерв, проверенный на этой сети':'Индивидуальные параметры, проверенные на этой сети'}{c.pageMs!=null?` · проверка страницы ${c.pageMs} мс`:''}</p>
 {!c.canApply&&<p className="rule-note">Нет подтверждённого выигрыша или недостаточно успешных замеров.</p>}
 <details><summary>DNS, IPv4/IPv6, TLS и содержимое</summary>{c.groups.map((g,i)=><p className="rule-note" key={`${g.id}-${i}`}>{g.id}: {g.state} · {g.detail}</p>)}</details></article>)}
 <details><summary>Сетевые параметры Firefox</summary><p className="rule-note">{status.firefox}</p></details></>}
 {['applied','browserConfirmed'].includes(status.state)&&<p>Проверьте Firefox: страницу, несколько видео и перемотку. Затем Telegram Web, звонок и украшения Discord.</p>}
 {status.state==='applied'&&<Button color="secondary" disabled={app.controlsBusy||!connected} onClick={()=>postNativePayload({type:'youtube.browser.confirm',profileId:profile.id})}>Я проверил Firefox: видео и перемотка работают</Button>}</>}
 {!busy&&<details><summary>Расширенный поиск</summary><p className="rule-note">До пяти индивидуальных вариантов и резерв; до 6 минут вместе с восстановлением. Обычная проверка остаётся до 180 секунд.</p><Button color="secondary" disabled={!connected||app.controlsBusy} onClick={()=>app.startYoutubeRun(profile.id,'extended')}>Запустить расширенный поиск · до 6 минут</Button></details>}
 <p className="rule-note">Настройки DNS, ECH и HTTP/3 Firefox автоматически не меняются. Проверка сертификатов сохраняется.</p></div>
 <div className="dialog-footer youtube-only-footer">
 {results&&blocked&&<p className="field-error" role="status" style={{flexBasis:'100%'}}>{blocked}</p>}
 <Button color="secondary" variant="ghost" onClick={close}>{busy?'Отменить':'Закрыть'}</Button>
 {!busy&&<Button color="secondary" disabled={app.controlsBusy} onClick={()=>connected?app.startYoutubeRun(profile.id):postNativePayload({type:'connections.launch',id:profile.id})}>{!connected?(app.protection?'Подключить выбранный профиль':'Подключить профиль'):own?'Проверить снова':'Проверить и подобрать только YouTube'}</Button>}
 {results&&selected&&<Button color="primary" disabled={!canApply} onClick={()=>selected.fullyConfirmed?apply(false):setConfirm(true)}>Применить только YouTube</Button>}
 </div></ProfileOverlay>
 {confirm&&<ProfileOverlay title="Применить YouTube с ограничениями?" onClose={()=>setConfirm(false)}>
 <div className="dialog-body"><p>{selected?.groups.some(g=>g.id.startsWith('tls-')&&g.state==='confirmed')&&selected.groups.some(g=>g.id==='images'&&g.state==='confirmed')?'Страница, TLS и картинки работают; видео или часть компонентов ещё не проверены.':'Страница подтверждена; часть компонентов и видео ещё не проверены.'} Применить только YouTube?</p><p className="rule-note">Перед сохранением выполняется свежая проверка TLS, страницы и картинки. Остальные сервисы сохраняются.</p>{blocked&&<p className="field-error">{blocked}</p>}</div>
 <div className="dialog-footer"><Button color="secondary" onClick={()=>setConfirm(false)}>Отмена</Button><Button color="primary" disabled={!canApply} onClick={()=>apply(true)}>Применить с ограничениями</Button></div></ProfileOverlay>}
 </>;
}
