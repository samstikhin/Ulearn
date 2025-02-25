import React from "react";
import CommentSendForm from "./CommentSendForm.js";

const nameOnlyUser = {
	id: "1",
	visibleName: "lol",
	avatarUrl: null,
};

const userWithAvatar = {
	id: "11",
	visibleName: "Vasiliy Terkin",
	avatarUrl:
		"https://staff.skbkontur.ru/content/images/default-user-woman.png",
};

interface State {
	id: number,
	sending: boolean,
}

interface Props {
	success: boolean,
}

class SendingCommentStory extends React.Component<Props, State> {
	state = {
		id: 1,
		sending: false,
	};

	render() {
		return (
			<CommentSendForm
				handleSubmit={ this.handleSubmit }
				commentId={ this.state.id }
				author={ nameOnlyUser }
				sending={ this.state.sending }
				isForInstructors={ false }
				isShowFocus={ { inSendForm: false, } }
			/>
		);
	}

	handleSubmit = () => {
		this.setState({
			sending: true,
		});

		setTimeout(() => {
			const newState = {
				sending: false,
				id: 0,
			};
			if(this.props.success) {
				newState.id = 2;
			}
			this.setState(newState);
		}, 500);
	};
}

export default {
	title: "Comments/CommentSendForm",
};

export const Default = (): React.ReactNode => (
	<div>
		<h2>Формы с разными кнопками отправки</h2>
		<h3>Оставить комментарий</h3>
		<CommentSendForm
			isForInstructors={ false }
			commentId={ 1 }
			author={ nameOnlyUser }
			sending={ false }
			isShowFocus={ { inSendForm: false, } }
		/>
		<h3>Отправить ответ на комментарий</h3>
		<CommentSendForm
			submitTitle={ "Отправить" }
			isForInstructors={ false }
			commentId={ 1 }
			author={ nameOnlyUser }
			sending={ false }
			isShowFocus={ { inSendForm: false, } }
		/>
		<h3>Редактировать комментарий с кнопкой отмены отправки</h3>
		<CommentSendForm
			handleCancel={ () => ({}) }
			submitTitle={ "Сохранить" }
			isForInstructors={ false }
			commentId={ 1 }
			author={ nameOnlyUser }
			sending={ false }
			isShowFocus={ { inSendForm: false, } }
		/>
		<h3>Форма в состоянии отправки</h3>
		<CommentSendForm
			isForInstructors={ false }
			commentId={ 2 }
			author={ userWithAvatar }
			sending={ true }
			isShowFocus={ { inSendForm: false, } }
		/>
		<h3>Успешная отправка комментария очищает поле ввода</h3>
		<SendingCommentStory success={ true }/>
		<h3>Ошибка при отправке комментария НЕ очищает поле ввода</h3>
		<SendingCommentStory success={ false }/>
	</div>
);

Default.storyName = 'default';
