import { Children, createContext, isValidElement, useContext, type ReactNode } from "react";
import { Select } from "@openai/apps-sdk-ui/components/Select";

const SelectLabel = createContext<string | undefined>(undefined);
function TriggerView({label}:{label:string}) {
 const name = useContext(SelectLabel);
 return <>{name&&<span className="sr-only">{name}: </span>}{label}</>;
}

// Adapter preserves existing value/onChange contracts while replacing native menus.
export default function DesignSelect({children,value,onChange,disabled,id,className,"aria-label":label}: {
 children:ReactNode; value:string|number; disabled?:boolean; id?:string; className?:string;
 "aria-label"?:string; onChange:(event:{target:{value:string}})=>void;
}) {
 const options:{value:string;label:string;disabled?:boolean}[]=[];
 const visit=(nodes:ReactNode)=>Children.forEach(nodes,node=>{
  if(!isValidElement<{value?:string|number;children?:ReactNode;disabled?:boolean}>(node))return;
  if(node.type==="option")options.push({value:String(node.props.value),label:String(node.props.children),disabled:node.props.disabled});
  else visit(node.props.children);
 });
 visit(children);
 return <div className={`design-select ${className??""}`}><SelectLabel.Provider value={label}><Select id={id} value={String(value)} options={options}
  disabled={disabled} size="lg" triggerClassName="design-select-trigger" optionClassName="design-select-option"
  TriggerView={TriggerView} dropdownIconType="chevronDown" listMinWidth={180} listMaxWidth={420} align="start"
  onChange={option=>onChange({target:{value:option.value}})}/></SelectLabel.Provider></div>;
}
