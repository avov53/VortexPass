import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Button } from '@openai/apps-sdk-ui/components/Button';
import { Tooltip } from '@openai/apps-sdk-ui/components/Tooltip';
import { Switch } from '@openai/apps-sdk-ui/components/Switch';
import { Icon, type IconName } from './Icon';
import ProfileWorkspace from './ProfileWorkspace';
import WarpWorkspace from './WarpWorkspace';

type Page = 'home'|'profiles'|'warp'|'activity'|'settings';
type Category = 'general'|'appearance'|'compatibility';
const pages:{id:Page;name:string;icon:IconName}[]=[{id:'home',name:'Подключение',icon:'home'},{id:'profiles',name:'Профили',icon:'space'},{id:'warp',name:'Cloudflare WARP',icon:'globe'},{id:'activity',name:'Журнал',icon:'clock'}];
const categories:{id:Category;name:string;icon:IconName}[]=[{id:'general',name:'Общие',icon:'settings'},{id:'appearance',name:'Внешний вид',icon:'sun'},{id:'compatibility',name:'Совместимость',icon:'phone'}];
function Tip({label,children,side='right'}:{label:string;children:ReactNode;side?:'right'|'bottom'}) { return <Tooltip content={label} openDelay={350} side={side} sideOffset={6} maxWidth="none" contentClassName="reference-tooltip">{children}</Tooltip>; }
function Toggle({label,checked,onChange}:{label:string;checked:boolean;onChange:(v:boolean)=>void}) { return <Switch className="reference-switch" label={<span className="sr-only">{label}</span>} checked={checked} onCheckedChange={onChange}/>; }
function Row({title,description,children}:{title:string;description?:string;children:ReactNode}) { return <div className="setting-row"><div className="row-copy"><h3>{title}</h3>{description&&<p>{description}</p>}</div><div className="row-control">{children}</div></div>; }
export default function App(){
  const [page,setPage]=useState<Page>('home');
  const [category,setCategory]=useState<Category>('general');
  const [warp,setWarp]=useState(false);
  const [logs,setLogs]=useState<{time:string;message:string}[]>([]);
  const [light,setLight]=useState(false);
  const [tray,setTray]=useState(true);
  const [startup,setStartup]=useState(false);
  const [mobile,setMobile]=useState(false);
  const [message,setMessage]=useState('');
  const [menu,setMenu]=useState(false);
  const noticeTimer=useRef<ReturnType<typeof setTimeout>>();
  const scrollRef=useRef<HTMLElement>(null);
  const settings=page==='settings';
  const title=settings?categories.find(c=>c.id===category)!.name:pages.find(p=>p.id===page)!.name;
  useEffect(()=>{document.documentElement.dataset.theme=light?'light':'dark';return ()=>{delete document.documentElement.dataset.theme}},[light]);
  useEffect(()=>()=>clearTimeout(noticeTimer.current),[]);
  useEffect(()=>{scrollRef.current?.scrollTo(0,0)},[page,category]);
  useEffect(()=>{function key(e:KeyboardEvent){if(e.key==='Escape'){setMenu(false);}}document.addEventListener('keydown',key);return ()=>document.removeEventListener('keydown',key)},[]);
  const notify=(text:string)=>{setMessage(text);clearTimeout(noticeTimer.current);noticeTimer.current=setTimeout(()=>setMessage(''),2800)};
  const log=(text:string)=>setLogs(old=>[{time:new Date().toLocaleTimeString('ru-RU'),message:text},...old]);
  const navigate=(next:Page)=>{setPage(next);setMenu(false);};
  return <div className={`app-shell ${settings?'settings-mode':''}`}>
    <aside className="rail" aria-label="Разделы приложения">
      <div className="rail-top">{pages.map(p=><Tip label={p.name} key={p.id}><button className={`rail-button ${page===p.id?'active':''}`} aria-label={p.name} aria-current={page===p.id?'page':undefined} onClick={()=>navigate(p.id)}><Icon name={p.icon} size={23}/></button></Tip>)}<Tip label="Настройки"><button className={`rail-button ${settings?'active':''}`} aria-label="Настройки" onClick={()=>navigate('settings')}><Icon name="settings" size={23}/></button></Tip></div>

    </aside>
    <section className="workspace">
      <header className="workspace-header"><Icon name={settings?'settings':'folder'} size={19}/><strong>{settings?'Настройки':title}</strong><div className="header-actions"><span className="demo-badge">Прототип</span><Tip label="Дополнительные действия" side="bottom"><button className="subtle-icon" aria-label="Дополнительные действия" aria-expanded={menu} onClick={()=>setMenu(v=>!v)}><Icon name="more"/></button></Tip></div>{menu&&<><button className="menu-scrim" onClick={()=>setMenu(false)} aria-label="Закрыть меню"/><div className="action-menu"><button onClick={()=>{setLight(v=>!v);setMenu(false)}}><Icon name="sun"/>{light?'Тёмная тема':'Светлая тема'}</button><button onClick={()=>{navigate('settings')}}><Icon name="settings"/>Настройки</button><button onClick={()=>navigate('profiles')}><Icon name="space"/>Мои профили</button></div></>}</header>
      <main className="content-scroll" ref={scrollRef}><div className={`page-content ${settings?'settings-content':''}`}>
        <ProfileWorkspace page={page} onNavigate={navigate} onLog={log} notify={notify} warp={warp}/>
        <WarpWorkspace visible={page==='warp'} onConnectedChange={setWarp} onLog={log} notify={notify}/>
        {page==='activity'&&<><div className="page-heading"><h1>Журнал</h1><p>События текущей демонстрационной сессии.</p></div><div className="section-title"><h2>Последние события</h2><Button color="secondary" variant="ghost" size="sm" onClick={()=>setLogs([])}>Очистить</Button></div><div className="settings-group">{logs.length?logs.map((l,i)=><div className="log-row" key={`${l.time}-${i}`}><time>{l.time}</time><span>{l.message}</span></div>):<div className="empty-state"><Icon name="clock" size={30}/><h2>Пока нет событий</h2><p>Подключите профили, чтобы увидеть их здесь.</p></div>}</div></>}
        {settings&&<><div className="page-heading"><h1>Настройки</h1></div><nav className="settings-tabs" aria-label="Категории настроек">{categories.map(c=><button key={c.id} className={category===c.id?'active':''} aria-current={category===c.id?'page':undefined} onClick={()=>setCategory(c.id)}><Icon name={c.icon}/>{c.name}</button>)}</nav>{category==='general'&&<><h2 className="section-label">Запуск и окно</h2><div className="settings-group"><Row title="Сворачивать в трей" description="Оставлять VortexPass в области уведомлений при закрытии окна"><Toggle label="Сворачивать в трей" checked={tray} onChange={setTray}/></Row><Row title="Запускать с Windows" description="Открывать приложение при входе в систему. WARP включается вручную."><Toggle label="Запускать с Windows" checked={startup} onChange={setStartup}/></Row></div><h2 className="section-label">Общие</h2><div className="settings-group"><Row title="Язык" description="Язык пользовательского интерфейса"><span className="value-pill">Русский</span></Row><Row title="Версия приложения" description="Самостоятельный дизайн-прототип VortexPass"><span className="plain-value">0.0.7</span></Row><Row title="Журнал событий" description="События подключения и настройки профилей"><Button color="secondary" variant="soft" size="md" pill={false} onClick={()=>navigate('activity')}>Открыть</Button></Row></div></>}{category==='appearance'&&<><h2 className="section-label">Оформление</h2><div className="settings-group"><Row title="Светлая тема" description="Изменить оформление пространства"><Toggle label="Светлая тема" checked={light} onChange={setLight}/></Row><Row title="Анимации" description="Учитывается системное предпочтение «Уменьшить движение»"><span className="plain-value">Системные</span></Row></div></>}{category==='compatibility'&&<><h2 className="section-label">Сеть</h2><div className="settings-group"><Row title="Мобильная совместимость" description="Режим для подключения через мобильную сеть"><Toggle label="Мобильная совместимость" checked={mobile} onChange={setMobile}/></Row></div></>}<p className="page-note">Параметры хранятся в этой вкладке и не меняют настройки Windows.</p></>}
      </div><footer>Демонстрационный интерфейс · сетевые команды не выполняются</footer></main>
    </section>
    <div className={`notice ${message?'visible':''}`} role="status">{message}</div>
  </div>;
}
