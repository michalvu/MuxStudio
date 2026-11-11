/* PROSÍM NEPOUŽÍVEJTE PŘÍMÉ LINKY, BEZ ČASOVÉHO ODPOČTU NEBUDOU FUNKČNÍ */

function CountDown(t){
 if (t-->1){
   setTimeout("CountDown("+t+")",15e2);
   document.getElementById('count_down').innerHTML=t;
 }
 else{
  document.getElementById('count_down').style.display='none';
  document.getElementById('downdiv').style.display='block';
  document.getElementById('razenidiv').style.display='none';

  ClearInfo('');
 }
}

function ShowLoader(show){
	document.getElementById('imgLoader').style.display=show;
}


function FinishDown(){
  var link=document.getElementById('downlink');
  //document.location=link.getAttribute('href');
  var tit=link.getAttribute('href');
  window.open(tit);
  //AfterDownload();
}

function ClearInfo(s){
  try{
   parent.document.getElementById('downinfo').innerHTML=s;
  }
  catch(err){
  }
}

function AfterDownload(){
	document.getElementById('afterdown').style.display='block';
}
