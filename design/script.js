const profiles = [
  {id:'youtube',name:'YouTube',description:'Видео и трансляции',symbol:'▷'},
  {id:'discord',name:'Discord',description:'Общение и голосовая связь',symbol:'♧'},
  {id:'telegram',name:'Telegram',description:'Desktop и Web',symbol:'➤'},
  {id:'web',name:'Web',description:'Повседневный интернет',symbol:'◎'},
  {id:'games',name:'Game Filter',description:'Профили для игр',symbol:'⌘'},
  {id:'mobile',name:'Совместимость',description:'Мобильные подключения',symbol:'▯'}
];
const state={page:'home',selected:new Set(['youtube','discord']),level:2,connected:false,warp:false,logs:[],settings:{tray:true,start:false,mobile:false},games:new Set(['Roblox'])};
const titles={home:'Подключение',profiles:'Профили',warp:'Cloudflare WARP',activity:'Журнал',settings:'Настройки'};
const content=document.getElementById('content');
let toastTimer;
function toast(message){const el=document.getElementById('toast');el.textContent=message;el.classList.add('visible');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('visible'),2800)}
function log(message){state.logs.unshift({time:new Date().toLocaleTimeString('ru-RU'),message})}
function profileCards(){return `<div class="profile-grid">${profiles.map(p=>`<button class="profile ${state.selected.has(p.id)?'selected':''}" data-profile="${p.id}" aria-pressed="${state.selected.has(p.id)}"><span class="profile-symbol" aria-hidden="true">${p.symbol}</span><span class="check" aria-hidden="true">${state.selected.has(p.id)?'✓':''}</span><strong>${p.name}</strong><small>${p.description}</small></button>`).join('')}</div>`}
function level(){return `<div class="level-row"><div><h2>Уровень настройки</h2><small>${['Мягкий','Сбалансированный','Усиленный','Активный','Максимальный'][state.level-1]}</small></div><div class="level-control" aria-label="Уровень настройки">${[1,2,3,4,5].map(n=>`<button class="level-button ${n===state.level?'active':''}" data-level="${n}" aria-pressed="${n===state.level}">${n}</button>`).join('')}</div></div>`}
function intro(title,description){return `<div class="page-intro"><div class="eyebrow">Ваше пространство</div><h1>${title}</h1><p>${description}</p></div>`}
function switchButton(key,value){return `<button class="switch" role="switch" aria-label="${key==='warp'?'Cloudflare WARP':key==='mobile'?'Мобильная совместимость':key==='tray'?'Сворачивать в трей':'Автозапуск'}" aria-checked="${value}" data-switch="${key}"></button>`}
function render(){
  document.getElementById('page-title').textContent=titles[state.page];
  document.querySelectorAll('[data-page]').forEach(el=>el.classList.toggle('active',el.dataset.page===state.page));
  if(state.page==='home')content.innerHTML=`<section class="hero"><div class="hero-mark" aria-hidden="true">V</div><div class="eyebrow">Интернет в вашем ритме</div><h1>Что подключим сегодня?</h1><p>Выберите сервисы и настройте соединение под себя.</p></section><section class="connection"><div class="connection-top"><div class="status"><span class="status-dot ${state.connected?'on':''}"></span>${state.connected?'Соединение активно':'Готов к подключению'}</div><span class="pill">Локальный режим</span></div><p>${state.connected?'Демонстрация активного состояния. Вы можете изменить набор профилей.':'Ваши профили собраны. Запустите их одним нажатием.'}</p><div class="connect-action"><small>${state.selected.size} профил${state.selected.size===1?'ь':state.selected.size>=2&&state.selected.size<=4?'я':'ей'} · уровень ${state.level} · ${state.warp?'WARP включён':'WARP выключен'}</small><button class="primary" id="connect">${state.connected?'Отключить':'Подключить'} <span aria-hidden="true">${state.connected?'○':'↑'}</span></button></div></section><div class="section-heading"><h2>Ваши сервисы</h2><button class="text-button" id="autotune">Автоподбор ↗</button></div>${profileCards()}${level()}<div class="hint">Основные профили работают локально. WARP подключается отдельно.</div>`;
  if(state.page==='profiles')content.innerHTML=intro('Ваш набор сервисов','Выбирайте несколько профилей и собирайте удобное подключение.')+profileCards()+level()+`<div class="section-heading"><h2>Game Filter</h2><span class="pill">3 игры</span></div><div class="panel">${['Roblox','Phasmophobia','Dead by Daylight'].map(game=>`<div class="row"><div><h2>${game}</h2><p>Отдельный профиль соединения</p></div><button class="switch" role="switch" aria-label="${game}" aria-checked="${state.games.has(game)}" data-game="${game}"></button></div>`).join('')}</div>`;
  if(state.page==='warp')content.innerHTML=intro('Cloudflare WARP','Управление официальным клиентом Cloudflare в одном пространстве.')+`<div class="panel"><div class="row"><div><div class="eyebrow">Официальный клиент · демосостояние</div><h2>${state.warp?'Туннель подключён':'Туннель отключён'}</h2><p>Подключение включается вручную и действует на систему.</p></div>${switchButton('warp',state.warp)}</div><div class="row"><div><h2>Диагностика соединения</h2><p>Внешний маршрут, DNS и доступность сервисов</p></div><button class="text-button" id="diagnose">Проверить ↗</button></div></div><div class="section-heading"><h2>Сценарии проверки</h2></div><div class="profile-grid">${['Нейросети','Telegram','Игры'].map((name,i)=>`<button class="profile" data-scenario="${name}"><span class="profile-symbol">${['✳','➤','⌘'][i]}</span><strong>${name}</strong><small>Проверить доступность</small></button>`).join('')}</div><p class="hint">Сценарии выбирают проверки. Маршрутизацией управляет клиент Cloudflare.</p>`;
  if(state.page==='activity')content.innerHTML=intro('Журнал активности','События этой демонстрационной сессии.')+`<div class="section-heading"><h2>Последние события</h2><button class="text-button" id="clear-log">Очистить</button></div><div class="panel">${state.logs.length?state.logs.map(entry=>`<div class="log-row"><time>${entry.time}</time><span>${entry.message}</span></div>`).join(''):'<div class="log-empty">Здесь появятся события после подключения.</div>'}</div>`;
  if(state.page==='settings')content.innerHTML=intro('Настройки пространства','Небольшие детали для повседневной работы.')+`<div class="panel"><div class="row"><div><h2>Светлая тема</h2><p>Изменить оформление интерфейса</p></div><button class="switch" role="switch" aria-label="Светлая тема" aria-checked="${document.body.classList.contains('light')}" data-switch="theme"></button></div>${[['tray','Сворачивать в трей','Оставлять приложение доступным в области уведомлений'],['start','Запускать с Windows','Открывать приложение при входе в систему'],['mobile','Мобильная совместимость','Режим для подключения через мобильную сеть']].map(([key,title,desc])=>`<div class="row"><div><h2>${title}</h2><p>${desc}</p></div>${switchButton(key,state.settings[key])}</div>`).join('')}</div><p class="hint">Параметры показаны для оценки дизайна и хранятся только в этой вкладке.</p>`;
  content.classList.remove('screen-enter');
  void content.offsetWidth;
  content.classList.add('screen-enter');
}
document.addEventListener('click',event=>{
  const button=event.target.closest('button');if(!button)return;
  if(button.dataset.page){state.page=button.dataset.page;if(innerWidth<=760)document.body.classList.add('collapsed');render()}
  if(button.dataset.profile){const id=button.dataset.profile;state.selected.has(id)?state.selected.delete(id):state.selected.add(id);render()}
  if(button.dataset.level){state.level=Number(button.dataset.level);render()}
  if(button.dataset.game){const game=button.dataset.game;state.games.has(game)?state.games.delete(game):state.games.add(game);render()}
  if(button.dataset.switch){const key=button.dataset.switch;if(key==='theme')document.body.classList.toggle('light');else if(key==='warp'){state.warp=!state.warp;log(state.warp?'WARP: демонстрация подключения':'WARP: демонстрация отключения');toast('Изменено демонстрационное состояние WARP')}else state.settings[key]=!state.settings[key];render()}
  if(button.dataset.scenario)toast(`Проверка «${button.dataset.scenario}» доступна в приложении`);
  if(button.id==='connect'){if(!state.connected&&!state.selected.size){toast('Выберите хотя бы один профиль');return}state.connected=!state.connected;log(state.connected?'Профили подключены (демонстрация)':'Профили отключены (демонстрация)');render()}
  if(button.id==='autotune'){state.level=2;log('Выбран сбалансированный уровень (демонстрация)');render();toast('В прототипе выбран сбалансированный уровень')}
  if(button.id==='diagnose')toast('Диагностика станет доступна после переноса в приложение');
  if(button.id==='clear-log'){state.logs=[];render()}
  if(button.id==='new-session'){state.selected=new Set();state.connected=false;state.page='home';render();toast('Выберите сервисы для нового подключения')}
  if(button.id==='theme-toggle'){document.body.classList.toggle('light');render()}
  if(button.id==='menu-toggle'){document.body.classList.toggle('collapsed');button.setAttribute('aria-expanded',String(!document.body.classList.contains('collapsed')))}
});
if(innerWidth<=760)document.body.classList.add('collapsed');
document.getElementById('menu-toggle').setAttribute('aria-expanded',String(!document.body.classList.contains('collapsed')));
render();
