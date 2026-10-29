import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { Button } from '@openai/apps-sdk-ui/components/Button';
import { Icon } from './Icon';
import LevelSlider from './LevelSlider';
import ProfileOverlay from './ProfileOverlay';
import SiteRulesEditor from './SiteRulesEditor';
import { countLabel,createProfile,draftErrors,emptyDraft,gameOptions,levelNames,normalizeDomains,serviceOptions,toDraft,type ProfileDraft,type UserProfile } from './profiles';

const variantCounts=[3,6,5,6,10];
const bypassDescriptions=[
  'Щадящий обход: минимум вмешательства и небольшой охват правил.',
  'Сбалансированный обход: умеренное покрытие выбранных сервисов.',
  'Усиленный обход: более широкий набор правил для выбранных сервисов.',
  'Активный обход: интенсивное применение правил и альтернативных маршрутов.',
  'Максимальный обход: самый широкий охват; возможны задержка и проблемы совместимости.'
];
const steps=[
  {at:0,text:'Не закрывайте приложение — мы создаём профиль',detail:'Собираем сведения о провайдере'},
  {at:3000,text:'Проверяем качество маршрута',detail:'Сравниваем DNS и задержку'},
  {at:6000,text:'Подбираем варианты обхода',detail:'Сопоставляем настройки профиля'},
  {at:9000,text:'Проверяем совместимость',detail:'Готовим варианты для сравнения'}
];
const variantTitles=['Рекомендуемый','Быстрее','Стабильнее','Для видео','Для общения','Для игр','Для сайтов','Сбалансированный','Расширенный','Максимальная совместимость'];
const variantNotes=['Лучший общий баланс','Приоритет скорости','Упор на стабильность','Настроен для потокового видео','Подходит для звонков и чатов','Для игровых соединений','Для повседневных сайтов','Универсальный вариант','Расширенный набор правил','Широкая совместимость'];
type Candidate={profile:UserProfile;title:string;note:string;score:number;latency:number};
function candidatesFor(draft:ProfileDraft,count:number):Candidate[]{
  return Array.from({length:count},(_,index)=>{
    const suffix=` · вариант ${index+1}`;
    const name=`${draft.name.trim().slice(0,80-suffix.length)}${suffix}`;
    const profile=createProfile({...draft,name});
    return {profile,title:variantTitles[index]??`Вариант ${index+1}`,note:variantNotes[index]??'Альтернативный набор параметров',score:Math.max(72,97-index*2),latency:[38,24,32,45,28,20,35,26,42,30][index]??40};
  });
}
export default function ProfileEditor({profile,onSave,onClose}:{profile?:UserProfile;onSave:(profiles:UserProfile[])=>void;onClose:()=>void}){
  const initial=useRef(profile?toDraft(profile):emptyDraft());
  const [draft,setDraft]=useState(initial.current);
  const [attempted,setAttempted]=useState(false);
  const [confirm,setConfirm]=useState(false);
  const [stage,setStage]=useState<'editing'|'provider'|'generating'|'results'>('editing');
  const [provider,setProvider]=useState('Демо-провайдер');
  const [elapsed,setElapsed]=useState(0);
  const [candidates,setCandidates]=useState<Candidate[]>([]);
  const [chosen,setChosen]=useState<string[]>([]);
  const errors=draftErrors(draft);
  const dirty=JSON.stringify(initial.current)!==JSON.stringify(draft);
  const count=variantCounts[draft.level-1];
  const close=()=>{
    if(stage==='generating')return;
    if(dirty)setConfirm(true);else onClose();
  };
  const begin=()=>{
    setAttempted(true);
    setConfirm(false);
    if(Object.values(errors).some(Boolean))return;
    if(profile){onSave([createProfile(draft,profile.id)]);onClose();return}
    setStage('provider');
  };
  useEffect(()=>{
    if(stage!=='generating')return;
    const started=Date.now();setElapsed(0);
    const timer=window.setInterval(()=>{
      const next=Math.min(12000,Date.now()-started);setElapsed(next);
      if(next>=12000){window.clearInterval(timer);const rank=(c:Candidate)=>c.score*.7+(100-c.latency)*.3;const generated=candidatesFor(draft,count).sort((a,b)=>rank(b)-rank(a)||a.latency-b.latency).slice(0,5);setCandidates(generated);setChosen(generated[0]?[generated[0].profile.id]:[]);setStage('results')}
    },100);
    return ()=>window.clearInterval(timer);
  },[stage]);
  const toggle=(field:'services'|'games',id:string)=>setDraft(old=>({...old,[field]:old[field].includes(id)?old[field].filter(s=>s!==id):[...old[field],id]}));
  const activeStep=[...steps].reverse().find(step=>elapsed>=step.at)??steps[0];
  const remaining=Math.max(0,Math.ceil((12000-elapsed)/1000));
  const saveChosen=()=>{
    if(!chosen.length)return;
    const selected=candidates.filter(candidate=>chosen.includes(candidate.profile.id)).map(candidate=>candidate.profile);
    onSave(selected);onClose();
  };
  const editor=<form className="editor-form" onSubmit={e=>{e.preventDefault();begin()}}>
    <div className="editor-body">
      <label className="field-label" htmlFor="profile-name">Название</label><input id="profile-name" data-autofocus className="profile-input" placeholder="Например, Видео и общение" maxLength={80} value={draft.name} onChange={e=>setDraft({...draft,name:e.target.value})} aria-invalid={attempted&&!!errors.name} aria-describedby={attempted&&errors.name?'name-error':undefined}/>{attempted&&errors.name&&<p className="field-error" id="name-error">{errors.name}</p>}
      <div className="editor-section-title"><h3>Сервисы</h3><p>Добавьте нужные сценарии в свой профиль.</p></div>
      <div className="editor-services">{serviceOptions.map(s=><button type="button" className={`service-card ${draft.services.includes(s.id)?'selected':''}`} key={s.id} aria-pressed={draft.services.includes(s.id)} onClick={()=>toggle('services',s.id)}><Icon name={s.icon}/><span><strong>{s.name}</strong><small>{s.description}</small></span><span className="service-check">{draft.services.includes(s.id)&&<Icon name="check" size={12}/>}</span></button>)}</div>
      <div className="editor-section-title"><h3>Игры</h3><p>Можно выбрать игры независимо от общих сервисов.</p></div><div className="game-choices">{gameOptions.map(game=><label key={game}><input type="checkbox" checked={draft.games.includes(game)} onChange={()=>toggle('games',game)}/>{game}</label>)}</div>
      <div className="editor-section-title"><label className="field-label" htmlFor="profile-domains">Домены</label><p>Один домен на строку. Для всех поддоменов: *.example.com</p></div><textarea id="profile-domains" className="profile-input domains-input" placeholder={'example.com\nmedia.example.com\n*.example.org'} value={draft.domainsText} onChange={e=>setDraft({...draft,domainsText:e.target.value})} onBlur={()=>{const result=normalizeDomains(draft.domainsText);if(!result.error)setDraft(old=>({...old,domainsText:result.domains.join('\n')}))}} aria-invalid={!!errors.domains} aria-describedby={errors.domains?'domain-error':undefined}/>{errors.domains&&<p className="field-error" id="domain-error">{errors.domains}</p>}
      <SiteRulesEditor rules={draft.siteRules} onChange={siteRules=>setDraft(old=>({...old,siteRules}))}/>{attempted&&errors.rules&&<p className="field-error">{errors.rules}</p>}
      <div className="editor-section-title"><h3>Интенсивность обхода</h3><div className="bypass-description">{bypassDescriptions.map((description,index)=><p key={description} className={index===draft.level-1?'current':''} aria-hidden={index!==draft.level-1}>{description}</p>)}</div></div><div className="editor-slider"><LevelSlider value={draft.level} onChange={level=>setDraft(old=>({...old,level}))}/></div>
      {!profile&&<p className="generation-count-hint">На этом уровне будет подготовлено <strong>{countLabel(count,['вариант','варианта','вариантов'])} обхода</strong>.</p>}
      {attempted&&errors.content&&<p className="field-error">{errors.content}</p>}
      {profile&&<p className="page-note">Изменения активного профиля применятся при следующем подключении.</p>}
    </div>
    <div className="dialog-footer"><Button type="button" color="secondary" variant="ghost" onClick={close}>Отмена</Button><Button type="submit" color="primary">{profile?'Сохранить':'Подобрать профиль'}</Button></div>
  </form>;
  const providerStep=<><div className="generation-body">
    <div className="generation-intro"><span className="generation-icon"><Icon name="globe" size={24}/></span><h3>Проверим подключение</h3><p>Предполагаемый провайдер — демо-пример. В настоящем приложении его нужно будет подтвердить или исправить.</p></div>
    <label className="field-label" htmlFor="provider-name">Интернет-провайдер</label><input id="provider-name" data-autofocus className="profile-input" value={provider} maxLength={80} onChange={e=>setProvider(e.target.value)}/>
    <div className="generation-demo-note"><span className="demo-badge">ДЕМО</span><span>Диагностика DNS и маршрута будет показана как пример. Данные не отправляются в сеть.</span></div>
    <div className="generation-level-summary"><span>Интенсивность обхода: {levelNames[draft.level-1]}</span><strong>{countLabel(count,['вариант','варианта','вариантов'])}</strong><small>{bypassDescriptions[draft.level-1]}</small></div>
    <p className="generation-checks-note">Для всех уровней выполняются одинаковые проверки: провайдер, DNS, маршрут и совместимость. Подбор занимает около 12 секунд.</p>
  </div><div className="dialog-footer"><Button color="secondary" variant="ghost" onClick={()=>setStage('editing')}>Назад</Button><Button color="primary" disabled={!provider.trim()} onClick={()=>setStage('generating')}>Создать варианты<Icon name="arrow" size={16}/></Button></div></>;
  const loadingStep=steps.indexOf(activeStep);
  const loading=<><div className="generation-body loading-body" aria-live="polite">
    <div className="generation-orbit" aria-hidden="true"><span/><span/><span/><Icon name="shield" size={30}/></div>
    <span className="demo-badge">ДЕМОНСТРАЦИЯ</span><h3 key={activeStep.text} className="generation-message">{activeStep.text}</h3><p className="generation-detail" key={activeStep.detail}>{activeStep.detail}</p>
    <div className="generation-progress" role="progressbar" aria-label="Создание вариантов профиля" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(elapsed/120)}><span style={{width:`${elapsed/120}%`} as CSSProperties}/></div>
    <div className="generation-progress-meta"><span>Этап {loadingStep+1} из {steps.length}</span><span>Осталось примерно {remaining} сек.</span></div>
    <p className="generation-provider">Провайдер: <strong>{provider.trim()}</strong></p>
  </div><div className="dialog-footer"><Button color="secondary" variant="ghost" onClick={()=>{setElapsed(0);setStage('editing')}}>Отменить подбор</Button></div></>;
  const results=<><div className="generation-body result-body"><div className="results-heading"><span className="results-success"><Icon name="check" size={18}/></span><div><h3>Варианты готовы</h3><p>Провайдер: {provider.trim()} · демонстрационный подбор</p></div></div><p className="results-select-hint">Отобраны лучшие {candidates.length} из {count} вариантов по скорости и совместимости. Выберите подходящие для сохранения.</p>
    <div className="variant-list">{candidates.map((candidate,index)=>{const checked=chosen.includes(candidate.profile.id);return <button type="button" key={candidate.profile.id} className={`variant-card ${checked?'chosen':''}`} aria-pressed={checked} onClick={()=>setChosen(old=>checked?old.filter(id=>id!==candidate.profile.id):[...old,candidate.profile.id])}><span className="variant-check">{checked&&<Icon name="check" size={12}/>}</span><span className="variant-copy"><strong>{candidate.title}</strong><small>{candidate.profile.name} · {candidate.note}</small><span>{candidate.profile.services.length?candidate.profile.services.map(id=>serviceOptions.find(s=>s.id===id)?.name??id).join(', '):'Свои домены и правила'}{candidate.profile.games.length?` · ${candidate.profile.games.join(', ')}`:''}</span></span><span className="variant-score">{candidate.score}<small>совместимость</small><small>{candidate.latency} мс · демо</small></span></button>})}</div>
    <div className="variant-list-actions"><button type="button" onClick={()=>setChosen(candidates.map(c=>c.profile.id))}>Выбрать все</button><button type="button" onClick={()=>setChosen([])}>Снять выбор</button></div><p className="generation-demo-note"><span className="demo-badge">ДЕМО</span><span>Оценки и варианты созданы локально для показа интерфейса и не являются результатом ИИ-анализа.</span></p>
  </div><div className="dialog-footer results-footer"><span>{chosen.length} выбрано</span><Button color="secondary" variant="ghost" onClick={()=>setStage('editing')}>Изменить настройки</Button><Button color="primary" disabled={!chosen.length} onClick={saveChosen}>Сохранить выбранные</Button></div></>;
  return <>{<ProfileOverlay title={profile?'Изменить профиль':stage==='editing'?'Создать профиль':stage==='provider'?'Подбор профиля':stage==='generating'?'Создаём профили':'Выберите варианты'} drawer onClose={close}>
    {stage==='editing'?editor:stage==='provider'?providerStep:stage==='generating'?loading:results}
  </ProfileOverlay>}{confirm&&<ProfileOverlay title="Сохранить изменения?" onClose={()=>setConfirm(false)}><div className="dialog-body"><p>В профиле есть несохранённые изменения.</p></div><div className="dialog-footer stacked-footer"><Button color="secondary" variant="ghost" onClick={()=>setConfirm(false)}>Продолжить редактирование</Button><Button color="secondary" variant="soft" onClick={onClose}>Не сохранять</Button><Button color="primary" onClick={begin}>Сохранить</Button></div></ProfileOverlay>}</>;
}
