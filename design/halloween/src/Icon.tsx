export type IconName = 'home'|'space'|'clock'|'globe'|'more'|'settings'|'search'|'edit'|'folder'|'panel'|'chevron'|'check'|'arrow'|'sun'|'phone'|'shield'|'bell'|'game'|'video'|'message'|'send'|'close';
const paths: Record<IconName, React.ReactNode> = {
  home:<><path d="m3 10 9-7 9 7"/><path d="M5 9v12h5v-7h4v7h5V9"/></>,
  space:<><rect x="5" y="7" width="13" height="15" rx="3" transform="rotate(-8 5 7)"/><path d="M9 7V5a3 3 0 0 1 3-3h6a3 3 0 0 1 3 3v10a3 3 0 0 1-3 3"/></>,
  clock:<><circle cx="12" cy="12" r="9"/><path d="M12 6v6l-3 2"/></>,
  globe:<><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c5 5 5 13 0 18-5-5-5-13 0-18"/></>,
  more:<><circle cx="5" cy="12" r=".6"/><circle cx="12" cy="12" r=".6"/><circle cx="19" cy="12" r=".6"/></>,
  settings:<><path d="m9 3-1 3-3 1-2 4 2 3v3l4 3 3-1 3 1 4-3v-3l2-3-2-4-3-1-1-3Z"/><circle cx="12" cy="12" r="3"/></>,
  search:<><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/></>,
  edit:<><path d="M13 4H6a3 3 0 0 0-3 3v11a3 3 0 0 0 3 3h11a3 3 0 0 0 3-3v-7"/><path d="m11 13 2-5 6-6 3 3-6 6Z"/></>,
  folder:<path d="M3 7V6a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7h18"/>,
  panel:<><rect x="3" y="4" width="18" height="16" rx="3"/><path d="M9 4v16"/></>,
  chevron:<path d="m7 10 5 5 5-5"/>, check:<path d="m5 12 4 4L19 6"/>, arrow:<><path d="M12 20V4m-6 6 6-6 6 6"/></>,
  sun:<><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1 1m12 12 1 1M5 19l1-1M18 6l1-1"/></>,
  phone:<><rect x="6" y="2" width="12" height="20" rx="3"/><path d="M10 18h4"/></>,
  shield:<><path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6Z"/><path d="m8 12 3 3 5-6"/></>,
  bell:<><path d="M5 17h14l-2-4V9a5 5 0 0 0-10 0v4Zm4 3h6"/></>,
  game:<><path d="M6 7h12l3 11a2 2 0 0 1-3 2l-4-3h-4l-4 3a2 2 0 0 1-3-2Z"/><path d="M6 12h6m-3-3v6m7-3h.1m2 2h.1"/></>,
  video:<><rect x="2" y="5" width="20" height="14" rx="4"/><path d="m10 9 5 3-5 3Z"/></>,
  message:<path d="M21 11a8 8 0 0 1-8 8H7l-4 3V7a5 5 0 0 1 5-5h5a8 8 0 0 1 8 9Z"/>,
  send:<><path d="m2 11 20-8-6 19-5-7-9-4Z"/><path d="m11 15 11-12"/></>,
  close:<path d="m6 6 12 12M6 18 18 6"/>
};
export function Icon({name,size=20}:{name:IconName;size?:number}) { return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>; }
